using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Data;
using NetPI.Host.Events;
using NetPI.Host.Sessions;

namespace NetPI.Host.Tests;

public static class SessionStoreTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Dir = T.TempDir("sessions");
        public readonly Database Db;
        public readonly EventBus Bus = new(NullLogger.Instance);
        public readonly SessionStore Store;
        public readonly List<BusEvent> Events = [];
        private readonly IDisposable _sub;

        public Fixture()
        {
            Db = new Database(Path.Combine(Dir, "netpi.db"));
            Store = new SessionStore(Db, Bus, Path.Combine(Dir, "workspace"));
            _sub = Bus.Subscribe("*", e => { lock (Events) Events.Add(e); });
        }

        public async Task<List<BusEvent>> EventsAsync(string type)
        {
            await Bus.FlushAsync();
            lock (Events) return Events.Where(e => e.Type == type).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            _sub.Dispose();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static ChatMessage Assistant(string text) => new()
    {
        Role = MessageRole.Assistant,
        Parts =
        [
            new ThinkingPart { Text = "hmm", Signature = "sig" },
            new TextPart { Text = text },
            new ToolCallPart { Id = "call_1", Name = "read", Arguments = """{"path":"a.txt"}""" },
        ],
        Provider = "aiproxy", Model = "m1", StopReason = "tool_use",
        Usage = new Usage { InputTokens = 10, OutputTokens = 5, CacheReadTokens = 2 },
        DurationMs = 1234,
        Meta = new JsonObject { ["kind"] = "test" },
    };

    public static void Register(TestRunner r)
    {
        r.Add("sessions: messages round-trip with polymorphic parts, usage and meta", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            Check.Equal(SessionStore.DefaultTitle, s.Title);
            Check.True(s.Id.StartsWith("ses_"));
            var m = f.Store.AppendMessage(s.Id, Assistant("hello"));
            Check.True(m.Id > 0);
            Check.Equal(1L, m.Seq);
            var tool = f.Store.AppendMessage(s.Id, new ChatMessage
            {
                Role = MessageRole.Tool,
                Parts = [new ToolResultPart { CallId = "call_1", Name = "read", Content = "data", Details = new JsonObject { ["lines"] = 3 }, Images = [new ImagePart { Data = "AAAA" }] }],
            });
            Check.Equal(2L, tool.Seq);
            var back = f.Store.GetMessage(m.Id)!;
            Check.Equal(MessageRole.Assistant, back.Role);
            Check.Equal(3, back.Parts.Count);
            Check.True(back.Parts[0] is ThinkingPart { Text: "hmm", Signature: "sig" });
            Check.Equal("hello", back.Text);
            Check.True(back.Parts[2] is ToolCallPart { Id: "call_1", Name: "read" });
            Check.Equal(10L, back.Usage!.InputTokens);
            Check.Equal(1234L, back.DurationMs);
            Check.Equal("test", back.MetaString("kind"));
            Check.Equal("tool_use", back.StopReason);
            var tr = (ToolResultPart)f.Store.GetMessage(tool.Id)!.Parts[0];
            Check.Equal(3, tr.Details!["lines"]!.GetValue<int>());
            Check.Equal("AAAA", tr.Images![0].Data);

            var json = JsonSerializer.Serialize(back, NetPiJson.Options);
            Check.Contains(json, "\"role\":\"assistant\"");
            Check.Contains(json, "\"type\":\"thinking\"");
            Check.Contains(json, "\"type\":\"tool_call\"");

            back.Parts.Add(new TextPart { Text = "more" });
            back.StopReason = "stop";
            f.Store.UpdateMessage(back);
            Check.Equal("hello\nmore", f.Store.GetMessage(m.Id)!.Text);
            Check.Equal(1, (await f.EventsAsync(EventTypes.MessageUpdated)).Count);
            var added = await f.EventsAsync(EventTypes.MessageAdded);
            Check.Equal(2, added.Count);
            Check.Equal(s.Id, added[0].SessionId, "message events are session scoped");
            var session = f.Store.GetSession(s.Id)!;
            Check.Equal(2L, session.MessageCount);
        });

        r.Add("sessions: auto-title from the first user message (first line, ≤ 60 chars)", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo { Title = "" });
            f.Store.AppendMessage(s.Id, ChatMessage.NoticeText("notice first", "system"));
            Check.Equal(SessionStore.DefaultTitle, f.Store.GetSession(s.Id)!.Title, "notices do not title");
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("  Fix the login bug in the authentication module please, it keeps failing on Windows\nsecond line"));
            var title = f.Store.GetSession(s.Id)!.Title;
            Check.True(title.Length <= 60, $"title length {title.Length}");
            Check.True(title.StartsWith("Fix the login bug"));
            Check.True(title.EndsWith("…"));
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("another message"));
            Check.Equal(title, f.Store.GetSession(s.Id)!.Title, "only the first user message titles");
            var named = f.Store.CreateSession(new SessionInfo { Title = "Custom" });
            f.Store.AppendMessage(named.Id, ChatMessage.UserText("short"));
            Check.Equal("Custom", f.Store.GetSession(named.Id)!.Title);
            var updated = await f.EventsAsync(EventTypes.SessionUpdated);
            Check.True(updated.Count >= 3);
            Check.True(updated.All(e => e.SessionId is null), "session.* events are broadcast");
        });

        r.Add("sessions: paging returns the page ending before beforeSeq, ascending", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            for (var i = 1; i <= 25; i++) f.Store.AppendMessage(s.Id, ChatMessage.UserText("m" + i));
            Check.Equal(25, f.Store.GetMessages(s.Id).Count);
            var last = f.Store.GetMessages(s.Id, null, 10);
            Check.Equal("16..25", $"{last[0].Seq}..{last[^1].Seq}");
            var before = f.Store.GetMessages(s.Id, 16, 10);
            Check.Equal("6..15", $"{before[0].Seq}..{before[^1].Seq}");
            var first = f.Store.GetMessages(s.Id, 6, 10);
            Check.Equal("1..5", $"{first[0].Seq}..{first[^1].Seq}");
            Check.Equal(4, f.Store.GetMessages(s.Id, 5).Count, "beforeSeq without limit");
        });

        r.Add("sessions: a fork copies the messages up to a seq (same seqs, times, parts, meta), then session.created and session.forked", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo { Title = "Chat", Model = "m1" });
            var u1 = f.Store.AppendMessage(s.Id, ChatMessage.UserText("first"));                                        // 1
            var skill = f.Store.AppendMessage(s.Id, new ChatMessage                                                     // 2
            {
                Role = MessageRole.Notice, Parts = [new TextPart { Text = "skill" }],
                Meta = new JsonObject { ["kind"] = "skill", ["for"] = u1.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            });
            var a1 = f.Store.AppendMessage(s.Id, Assistant("answer"));                                                   // 3
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("second"));                                                 // 4
            f.Store.AppendMessage(s.Id, Assistant("later"));                                                             // 5
            await f.Bus.FlushAsync();
            lock (f.Events) f.Events.Clear();

            var fork = f.Store.ForkSession(s.Id, 3, new SessionInfo { Title = "Chat (fork)", Model = "m1", Meta = new JsonObject { ["x"] = 1 } });
            Check.Equal(3, fork.MessageCount);
            var copy = f.Store.GetMessages(fork.Id);
            Check.Equal("1,2,3", string.Join(",", copy.Select(m => m.Seq)));
            Check.Equal("first|skill|answer", string.Join("|", copy.Select(m => m.Text)));
            Check.Equal(a1.CreatedAt, copy[2].CreatedAt, "the times of the original");
            Check.Equal("sig", ((ThinkingPart)copy[2].Parts[0]).Signature);
            Check.Equal(10, copy[2].Usage!.InputTokens);
            Check.Equal("test", copy[2].MetaString("kind"));
            Check.True(copy.All(m => m.SessionId == fork.Id) && copy[0].Id != u1.Id, "new messages");
            Check.Equal(copy[0].Id.ToString(System.Globalization.CultureInfo.InvariantCulture), copy[1].MetaString("for"), "a message id in meta.for names the copy");
            Check.Equal(u1.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), f.Store.GetMessage(skill.Id)!.MetaString("for"), "the original is as it was");
            Check.Equal(5, f.Store.GetMessages(s.Id).Count);
            Check.Equal(3, f.Store.GetSession(fork.Id)!.MessageCount);
            Check.Equal(1, (int)f.Store.GetSession(fork.Id)!.Meta!["x"]!);

            var created = (await f.EventsAsync(EventTypes.SessionCreated)).Single();
            Check.Equal(3, JsonSerializer.SerializeToNode(created.Data, NetPiJson.Options)!["session"]!["messageCount"]!.GetValue<int>(), "session.created once the copy is complete");
            var forked = JsonSerializer.SerializeToNode((await f.EventsAsync(EventTypes.SessionForked)).Single().Data, NetPiJson.Options)!;
            Check.Equal(fork.Id, forked["sessionId"]!.GetValue<string>());
            Check.Equal(s.Id, forked["fromSessionId"]!.GetValue<string>());
            Check.Equal(3L, forked["upToSeq"]!.GetValue<long>());
            Check.Equal(0, (await f.EventsAsync(EventTypes.MessageAdded)).Count, "copied in one statement, not message by message");
            Check.True(f.Store.ForkSession(s.Id, 0, new SessionInfo { Title = "empty" }).MessageCount == 0, "a fork before the first message is an empty chat");
        });

        r.Add("sessions: a fork is compacted as the original was at the fork point, not by a later compaction", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            for (var i = 1; i <= 6; i++) f.Store.AppendMessage(s.Id, ChatMessage.UserText("m" + i)); // 1..6
            // summary A at 7 covers 1..4; later m8, m9; summary B at 10 covers 1..8 and supersedes A (as the compaction plugin does)
            var a = f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.Summary, Parts = [new TextPart { Text = "summary A" }], Meta = new JsonObject { ["coversUpToSeq"] = 4 } });
            f.Store.MarkCompacted(s.Id, 4);
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("m8"));
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("m9"));
            f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.Summary, Parts = [new TextPart { Text = "summary B" }], Meta = new JsonObject { ["coversUpToSeq"] = 8 } });
            f.Store.MarkCompacted(s.Id, 8);
            a.Compacted = true;
            f.Store.UpdateMessage(a);
            Check.Equal("summary B,m9", string.Join(",", f.Store.GetContextMessages(s.Id).Select(m => m.Text)));

            string Context(long upTo) => string.Join(",", f.Store.GetContextMessages(f.Store.ForkSession(s.Id, upTo, new SessionInfo()).Id).Select(m => m.Text));
            Check.Equal("summary A,m5,m6,m8,m9", Context(9), "at 9 the original had summary A: B came later");
            Check.Equal("m1,m2,m3,m4,m5,m6", Context(6), "before any summary nothing is compacted");
            Check.Equal("summary B,m9", Context(10), "everything: as the original is now");
            Check.Equal("summary B,m9", string.Join(",", f.Store.GetContextMessages(s.Id).Select(m => m.Text)), "the original is untouched");
        });

        r.Add("sessions: what a fork takes along (setup yes, run state no), its title and its context size", () =>
        {
            var from = new SessionInfo
            {
                Id = "ses_a", Title = "Chat", ProjectId = "prj_1", Model = "m1", Reasoning = "high", Kind = "chat",
                Meta = new JsonObject
                {
                    ["profile"] = "coder", ["identity"] = "You are…", ["toolsOff"] = new JsonArray("bash"), ["agent"] = "qwen",
                    ["goal"] = new JsonObject { ["status"] = "active" }, ["todo"] = new JsonArray(), ["budgetAllowedFrom"] = "2026-09-01", ["guardrailsAllowed"] = new JsonArray("ask: ^git push"),
                    ["forkedFrom"] = new JsonObject { ["sessionId"] = "ses_z" },
                },
            };
            var t = SessionFork.Template(from, 7, 1234, new HashSet<string> { "Chat", "Chat (fork)" });
            Check.Equal("Chat (fork 2)", t.Title, "the first free number");
            Check.True(t is { ProjectId: "prj_1", Model: "m1", Reasoning: "high", Kind: "chat", ContextTokens: 1234 });
            Check.Equal("agent,forkedFrom,identity,profile,toolsOff", string.Join(",", t.Meta!.Select(kv => kv.Key).Order()));
            Check.Equal("ses_a", t.Meta!["forkedFrom"]!["sessionId"]!.GetValue<string>());
            Check.Equal(7L, t.Meta!["forkedFrom"]!["seq"]!.GetValue<long>());
            Check.Equal("Chat (fork)", SessionFork.Title("Chat"));
            Check.Equal("Chat (fork 2)", SessionFork.Title("Chat (fork)"), "a fork of a fork counts on");
            Check.Equal("Chat (fork 4)", SessionFork.Title("Chat (fork 2)", new HashSet<string> { "Chat (fork 2)", "Chat (fork 3)" }));
            Check.Equal(17L, SessionFork.ContextTokens([ChatMessage.UserText("x"), Assistant("a"), ChatMessage.UserText("y")]), "the last model call before the fork point");
            return Task.CompletedTask;
        });

        r.Add("sessions: context = non-compacted messages with the latest summary first", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            for (var i = 1; i <= 6; i++) f.Store.AppendMessage(s.Id, ChatMessage.UserText("m" + i)); // seq 1..6
            // First compaction: keep 5..6, summary appended at seq 7.
            f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.Summary, Parts = [new TextPart { Text = "summary A" }] });
            f.Store.MarkCompacted(s.Id, 4);
            var ctx = f.Store.GetContextMessages(s.Id);
            Check.Equal("summary A,m5,m6", string.Join(",", ctx.Select(m => m.Text)));
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("m8"));
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("m9"));
            // Second compaction: keep m9 (seq 9), summary B at seq 10, old summary compacted.
            f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.Summary, Parts = [new TextPart { Text = "summary B" }] });
            f.Store.MarkCompacted(s.Id, 8);
            ctx = f.Store.GetContextMessages(s.Id);
            Check.Equal("summary B,m9", string.Join(",", ctx.Select(m => m.Text)));
            Check.Equal(10, f.Store.GetMessages(s.Id).Count, "compacted messages are still listed for the UI");
            Check.True(f.Store.GetMessages(s.Id)[0].Compacted);
            var compacted = await f.EventsAsync(EventTypes.MessagesCompacted);
            Check.Equal(2, compacted.Count);
            Check.Equal(s.Id, compacted[0].SessionId);
        });

        // The host only stores the switch and publishes session.project; the conversation is appended to by plugins (the
        // context plugin's project notice), so the host never writes model-facing text.
        r.Add("sessions: the context cache stays warm across an append, and only a real change drops it", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            for (var i = 1; i <= 5; i++) f.Store.AppendMessage(s.Id, ChatMessage.UserText("m" + i));

            // The first read is the only one out of the database.
            Check.Equal("m1,m2,m3,m4,m5", string.Join(",", f.Store.GetContextMessages(s.Id).Select(m => m.Text)));
            var (hits0, reads0) = f.Store.ContextCache;
            Check.Equal(0L, hits0);
            Check.Equal(1L, reads0);

            // A turn appends its own messages; the next read must not pay for the history again.
            f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "a1" }] });
            f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "m6" }] });
            Check.Equal("m1,m2,m3,m4,m5,a1,m6", string.Join(",", f.Store.GetContextMessages(s.Id).Select(m => m.Text)));
            var (hits1, reads1) = f.Store.ContextCache;
            Check.Equal(1L, hits1, "the append extended the cached context");
            Check.Equal(1L, reads1, "and nothing was read from the database again");

            // The caller's list is its own: the cache hands out a copy, in database order.
            var mine = f.Store.GetContextMessages(s.Id).ToList();
            mine.Reverse();
            mine.Clear();
            Check.Equal("m1,m2,m3,m4,m5,a1,m6", string.Join(",", f.Store.GetContextMessages(s.Id).Select(m => m.Text)),
                "reordering or emptying the returned list changes nothing");

            // A message edited in place: the cached copy would be stale, so the entry goes.
            var edited = f.Store.GetMessages(s.Id).First(m => m.Text == "m3");
            edited.Parts = [new TextPart { Text = "m3 edited" }];
            f.Store.UpdateMessage(edited);
            var (hits2, reads2) = f.Store.ContextCache;
            Check.Equal("m1,m2,m3 edited,m4,m5,a1,m6", string.Join(",", f.Store.GetContextMessages(s.Id).Select(m => m.Text)));
            Check.Equal(hits2, f.Store.ContextCache.Hits, "the read after an edit is a read, not a hit");
            Check.Equal(reads2 + 1, f.Store.ContextCache.Reads);

            // Compaction: the context changes shape, so the cache goes too.
            f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.Summary, Parts = [new TextPart { Text = "summary" }] });
            f.Store.GetContextMessages(s.Id);
            f.Store.MarkCompacted(s.Id, 4);
            var reads3 = f.Store.ContextCache.Reads;
            Check.Equal("summary,m5,a1,m6", string.Join(",", f.Store.GetContextMessages(s.Id).Select(m => m.Text)));
            Check.Equal(reads3 + 1, f.Store.ContextCache.Reads, "compaction drops the cached context");
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("m8"));
            Check.Equal("summary,m5,a1,m6,m8", string.Join(",", f.Store.GetContextMessages(s.Id).Select(m => m.Text)), "and it is warm again after the next append");
            Check.Equal(reads3 + 1, f.Store.ContextCache.Reads, "which cost no read");
        });

        r.Add("sessions: an out-of-order append drops the cached context instead of corrupting its order", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            for (var i = 1; i <= 4; i++) f.Store.AppendMessage(s.Id, ChatMessage.UserText("m" + i));
            Check.Equal("m1,m2,m3,m4", string.Join(",", f.Store.GetContextMessages(s.Id).Select(m => m.Text)));

            // A message whose seq the cached context has already passed cannot be appended in order, so the entry is
            // dropped and the next read rebuilds it from the database (where the order is the truth). Appends to one
            // of appends can commit in either order, and a cache that is ahead of the database must not be trusted.
            f.Db.Execute("DELETE FROM messages WHERE session_id = @id AND seq > 2", new { id = s.Id });
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("m3 again"));
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("m4 again"));
            Check.Equal("m1,m2,m3 again,m4 again", string.Join(",", f.Store.GetContextMessages(s.Id).Select(m => m.Text)));
        });

        r.Add("sessions: project attach/detach publishes session.project with the new cwd; no message from the host", async () =>
        {
            await using var f = new Fixture();
            var projDir = T.TempDir("proj");
            var p = f.Store.CreateProject("", projDir);
            Check.Equal(Path.GetFileName(projDir), p.Name);
            var s = f.Store.CreateSession(new SessionInfo());
            Check.Equal(Path.Combine(f.Dir, "workspace"), f.Store.GetCwd(s));
            // Materialize first: an unannounced (no-message) session takes project changes without events
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("start"));
            await f.Bus.FlushAsync();
            lock (f.Events) f.Events.Clear();

            var attached = f.Store.SetSessionProject(s.Id, p.Id);
            Check.Equal(p.Id, attached.ProjectId);
            Check.Equal(p.Path, f.Store.GetCwd(attached));
            Check.Equal(1, f.Store.GetMessages(s.Id).Count, "the host appends nothing");
            var first = (await f.EventsAsync(EventTypes.SessionProject)).Single().As<System.Text.Json.Nodes.JsonObject>()!;
            Check.Equal(s.Id, (string?)first["sessionId"]);
            Check.Equal(p.Id, (string?)first["projectId"]);
            Check.Equal(p.Path, (string?)first["cwd"]);

            f.Store.SetSessionProject(s.Id, p.Id);
            Check.Equal(1, (await f.EventsAsync(EventTypes.SessionProject)).Count, "no event when nothing changes");

            var detached = f.Store.SetSessionProject(s.Id, null);
            Check.Equal<string?>(null, detached.ProjectId);
            var second = (await f.EventsAsync(EventTypes.SessionProject)).Last().As<System.Text.Json.Nodes.JsonObject>()!;
            Check.Equal<string?>(null, (string?)second["projectId"]);
            Check.Equal(Path.Combine(f.Dir, "workspace"), (string?)second["cwd"]);
            Check.Throws<KeyNotFoundException>(() => f.Store.SetSessionProject(s.Id, "prj_missing"));

            // Deleting a project detaches its sessions.
            f.Store.SetSessionProject(s.Id, p.Id);
            f.Store.DeleteProject(p.Id);
            Check.Equal<string?>(null, f.Store.GetSession(s.Id)!.ProjectId);
            Check.Equal(0, f.Store.ListProjects().Count);
            Check.Equal(1, (await f.EventsAsync(EventTypes.ProjectDeleted)).Count);
        });

        r.Add("sessions: list filters and delete cascades to messages and subagent sessions", async () =>
        {
            await using var f = new Fixture();
            var p = f.Store.CreateProject("P", T.TempDir("proj"));
            var parent = f.Store.CreateSession(new SessionInfo { Title = "Parent chat", ProjectId = p.Id });
            var child = f.Store.CreateSession(new SessionInfo { Title = "Child", Kind = "subagent", ParentSessionId = parent.Id });
            var grandChild = f.Store.CreateSession(new SessionInfo { Title = "Grandchild", Kind = "subagent", ParentSessionId = child.Id });
            var other = f.Store.CreateSession(new SessionInfo { Title = "Other 100%_match" });
            f.Store.UpdateSession(other.Id, x => x.Archived = true);
            foreach (var id in new[] { parent.Id, child.Id, grandChild.Id, other.Id }) f.Store.AppendMessage(id, ChatMessage.UserText("hi"));

            Check.Equal(1, f.Store.ListSessions(new SessionQuery()).Count, "subagents and archived hidden by default");
            Check.Equal(2, f.Store.ListSessions(new SessionQuery { IncludeArchived = true }).Count);
            Check.Equal(3, f.Store.ListSessions(new SessionQuery { IncludeSubagents = true }).Count);
            Check.Equal(child.Id, f.Store.ListSessions(new SessionQuery { ParentSessionId = parent.Id }).Single().Id);
            Check.Equal(parent.Id, f.Store.ListSessions(new SessionQuery { ProjectId = p.Id }).Single().Id);
            Check.Equal(other.Id, f.Store.ListSessions(new SessionQuery { Search = "100%_", IncludeArchived = true }).Single().Id);
            Check.Equal(0, f.Store.ListSessions(new SessionQuery { Search = "0%m", IncludeArchived = true }).Count, "LIKE wildcards are escaped");

            f.Store.DeleteSession(parent.Id);
            Check.True(f.Store.GetSession(parent.Id) is null);
            Check.True(f.Store.GetSession(child.Id) is null);
            Check.True(f.Store.GetSession(grandChild.Id) is null);
            Check.True(f.Store.GetSession(other.Id) is not null);
            Check.Equal(1L, f.Db.Scalar<long>("SELECT COUNT(*) FROM messages"));
            Check.Equal(3, (await f.EventsAsync(EventTypes.SessionDeleted)).Count);
            Check.Throws<KeyNotFoundException>(() => f.Store.DeleteSession(parent.Id));
        });

        r.Add("sessions: an old archive survives a newest-first window: archivedOnly filter and stable paging", async () =>
        {
            await using var f = new Fixture();
            // The oldest session of the store, archived: the newer actives below push it outside any newest-first window
            var hidden = f.Store.CreateSession(new SessionInfo { Title = "Old archive" });
            f.Store.AppendMessage(hidden.Id, ChatMessage.UserText("hello"));
            f.Store.UpdateSession(hidden.Id, x => x.Archived = true);

            for (var i = 1; i <= 201; i++)
            {
                var s = f.Store.CreateSession(new SessionInfo { Title = $"Active {i:D3}" });
                f.Store.AppendMessage(s.Id, ChatMessage.UserText("m" + i));
            }

            Check.Equal(201, f.Store.ListSessions(new SessionQuery { Limit = 5000 }).Count, "the default query sees active sessions only");
            Check.True(f.Store.ListSessions(new SessionQuery { Limit = 5000 }).All(s => s.Id != hidden.Id));

            // The shape of the old bug: a newest-first window over active + archived, with the archives filtered client-side
            var window = f.Store.ListSessions(new SessionQuery { IncludeArchived = true, Limit = 200 });
            Check.Equal(200, window.Count);
            Check.True(window.All(s => s.Id != hidden.Id), "the old archive is outside the newest-200 window");

            // The fix: an archived-only query returns it, with or without a search
            Check.Equal(hidden.Id, f.Store.ListSessions(new SessionQuery { ArchivedOnly = true }).Single().Id);
            Check.Equal(hidden.Id, f.Store.ListSessions(new SessionQuery { ArchivedOnly = true, Search = "Old archive" }).Single().Id);
            Check.Equal(0, f.Store.ListSessions(new SessionQuery { ArchivedOnly = true, Search = "Active" }).Count);

            // More archives: paging over the archived-only list never skips or repeats a session
            for (var i = 1; i <= 11; i++)
            {
                var a = f.Store.CreateSession(new SessionInfo { Title = $"Archive {i:D2}" });
                f.Store.AppendMessage(a.Id, ChatMessage.UserText("a" + i));
                f.Store.UpdateSession(a.Id, x => x.Archived = true);
            }
            var all = f.Store.ListSessions(new SessionQuery { ArchivedOnly = true, Limit = 5000 });
            Check.Equal(12, all.Count);

            var walked = new List<string>();
            var seen = new HashSet<string>();
            for (var off = 0; ; off += 5)
            {
                var page = f.Store.ListSessions(new SessionQuery { ArchivedOnly = true, Limit = 5, Offset = off });
                if (page.Count == 0) break;
                Check.True(page.All(s => s.Archived), $"an archived-only page at offset {off} never leaks an active session");
                foreach (var s in page) Check.True(seen.Add(s.Id), $"no session repeats across page boundaries: {s.Id}");
                walked.AddRange(page.Select(s => s.Id));
                if (page.Count < 5) break;
            }
            Check.Equal(12, walked.Count, "no session skipped at a page boundary");
            Check.Equal(string.Join(",", all.Select(s => s.Id)), string.Join(",", walked), "the paged walk matches the full list in order");
            Check.Equal(0, f.Store.ListSessions(new SessionQuery { ArchivedOnly = true, Limit = 5, Offset = 50 }).Count, "beyond the end: empty");
        });

        r.Add("sessions: update mutates, persists and publishes session.updated", async () =>
        {
            await using var f = new Fixture();
            // While transient (no messages): updates are in memory only, nothing is announced
            var s = f.Store.CreateSession(new SessionInfo { Model = "aiproxy/a", Meta = new JsonObject { ["x"] = 1 } });
            f.Store.UpdateSession(s.Id, x => { x.Model = "anthropic/b"; x.Reasoning = "high"; x.Title = "Renamed"; });
            Check.Equal("anthropic/b", f.Store.GetSession(s.Id)!.Model);
            Check.Equal(0, (await f.EventsAsync(EventTypes.SessionUpdated)).Count, "a no-message session is not announced");
            Check.Equal(0, (await f.EventsAsync(EventTypes.SessionCreated)).Count, "no session.created until the first message");

            // Once it has a message, updates persist (the transient changes ride along) and are announced
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("hello"));
            await f.Bus.FlushAsync();
            lock (f.Events) f.Events.Clear();
            var u = f.Store.UpdateSession(s.Id, x => { x.ContextTokens = 1234; });
            var back = f.Store.GetSession(s.Id)!;
            Check.Equal("anthropic/b", back.Model, "the transient update survived materialization");
            Check.Equal("high", back.Reasoning);
            Check.Equal(1234L, back.ContextTokens);
            Check.Equal("Renamed", back.Title);
            Check.Equal(1, back.Meta!["x"]!.GetValue<int>());
            Check.True(u.UpdatedAt >= s.UpdatedAt);
            Check.Throws<KeyNotFoundException>(() => f.Store.UpdateSession("ses_missing", _ => { }));
            var evs = await f.EventsAsync(EventTypes.SessionUpdated);
            Check.Equal(1, evs.Count);
        });

        r.Add("sessions: a meta change publishes session.changed with the keys that changed; a no-op or a field outside meta does not", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo { Meta = new JsonObject { ["profile"] = "coder", ["toolsOff"] = new JsonArray("probe") } });
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("hello"));
            await f.Bus.FlushAsync();
            lock (f.Events) f.Events.Clear();

            async Task<List<string>> Keys()
            {
                var evs = await f.EventsAsync(EventTypes.SessionChanged);
                lock (f.Events) f.Events.Clear();
                var keys = new List<string>();
                foreach (var e in evs)
                {
                    var d = e.As<JsonObject>()!;
                    keys.Add($"{d["sessionId"]}:{string.Join("+", ((JsonArray)d["keys"]!).Select(k => k!.GetValue<string>()))}");
                }
                return keys;
            }

            // a profile switch: three keys at once, as the profiles plugin writes them
            f.Store.UpdateSession(s.Id, x =>
            {
                x.Meta!["profile"] = "admin";
                x.Meta["identity"] = "You are terse.";
                x.Meta["toolsOff"] = new JsonArray();
            });
            Check.Equal($"{s.Id}:identity+profile+toolsOff", string.Join(" | ", await Keys()));

            // one key
            f.Store.UpdateSession(s.Id, x => x.Meta!["identity"] = "You are verbose.");
            Check.Equal($"{s.Id}:identity", string.Join(" | ", await Keys()));

            // the same value again, and a field outside meta: nothing changed, so nothing is announced
            f.Store.UpdateSession(s.Id, x => x.Meta!["identity"] = "You are verbose.");
            f.Store.UpdateSession(s.Id, x => { x.Title = "Renamed"; x.ContextTokens = 99; });
            Check.Equal("", string.Join(" | ", await Keys()));

            // a key removed counts as changed
            f.Store.UpdateSession(s.Id, x => x.Meta!.Remove("profile"));
            Check.Equal($"{s.Id}:profile", string.Join(" | ", await Keys()));

            // and the transient path (a chat with no message yet) publishes it too: the profiles plugin gives a new chat
            // its default profile before the first call, and that is exactly when the first tool set is decided
            var t = f.Store.CreateSession(new SessionInfo());
            f.Store.UpdateSession(t.Id, x => x.Meta = new JsonObject { ["profile"] = "admin" });
            Check.Equal($"{t.Id}:profile", string.Join(" | ", await Keys()));
        });

        r.Add("sessions: a no-message session is transient — in memory only, until its first message", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo { Title = "", Meta = new JsonObject { ["a"] = 1 } });
            Check.True(f.Store.GetSession(s.Id) is { Title: SessionStore.DefaultTitle });
            Check.Equal(0, f.Store.ListSessions(new SessionQuery()).Count, "not listed while empty");
            Check.Equal(0, (await f.EventsAsync(EventTypes.SessionCreated)).Count, "not announced");

            // a fresh store over the same database (a simulated restart) does not know it
            var freshDb = new Database(Path.Combine(f.Dir, "netpi.db"));
            var freshStore = new SessionStore(freshDb, f.Bus, Path.Combine(f.Dir, "workspace"));
            Check.True(freshStore.GetSession(s.Id) is null, "no row in the database");
            freshDb.Dispose();

            // first message: materialized — row, title, count, and the events in order
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("the first words\nand more"));
            Check.True(f.Store.ListSessions(new SessionQuery()).Any(x => x.Id == s.Id), "listed once it has a message");
            Check.Equal("the first words", f.Store.GetSession(s.Id)!.Title, "auto-title at materialization");
            Check.Equal(1L, f.Store.GetSession(s.Id)!.MessageCount);
            var freshDb2 = new Database(Path.Combine(f.Dir, "netpi.db"));
            var freshStore2 = new SessionStore(freshDb2, f.Bus, Path.Combine(f.Dir, "workspace"));
            Check.Equal(1L, freshStore2.GetSession(s.Id)!.MessageCount, "visible to a fresh store");
            freshDb2.Dispose();

            await f.Bus.FlushAsync();
            string[] seq;
            lock (f.Events) seq = f.Events.Select(e => e.Type).ToArray();
            var iCreated = Array.IndexOf(seq, EventTypes.SessionCreated);
            var iAdded = Array.IndexOf(seq, EventTypes.MessageAdded);
            var iUpdated = seq.LastIndexOf(EventTypes.SessionUpdated);
            Check.True(iCreated >= 0 && iCreated < iAdded && iAdded < iUpdated, $"order: {string.Join(" → ", seq)}");

            // deleting an abandoned (still empty) session: no error, no row, session.deleted published
            var empty = f.Store.CreateSession(new SessionInfo());
            f.Store.DeleteSession(empty.Id);
            Check.True(f.Store.GetSession(empty.Id) is null);
            Check.True(f.Store.ListSessions(new SessionQuery()).All(x => x.Id != empty.Id));
            Check.Throws<KeyNotFoundException>(() => f.Store.DeleteSession(empty.Id));
            var deleted = (await f.EventsAsync(EventTypes.SessionDeleted))
                .Select(e => JsonSerializer.SerializeToNode(e.Data, NetPiJson.Options)!["id"]?.GetValue<string>())
                .ToList();
            Check.True(deleted.Contains(empty.Id), $"session.deleted for the transient: {string.Join(", ", deleted)}");
        });

        r.Add("sessions: a fork with nothing to copy stays transient (no row, no events); a fork of an empty source too", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo { Title = "Chat" });
            Check.True(f.Store.GetMessages(s.Id).Count == 0);

            var fork = f.Store.ForkSession(s.Id, 0, new SessionInfo { Title = "Chat (fork)" });
            Check.Equal(0, fork.MessageCount);
            Check.True(f.Store.GetSession(fork.Id) is not null, "resolves while the store runs");
            Check.Equal(0, f.Store.ListSessions(new SessionQuery()).Count, "not listed");
            Check.Equal(0, (await f.EventsAsync(EventTypes.SessionCreated)).Count, "no session.created");
            Check.Equal(0, (await f.EventsAsync(EventTypes.SessionForked)).Count, "no session.forked");

            // the fork materializes when it gets its first message, like any session
            f.Store.AppendMessage(fork.Id, ChatMessage.UserText("now it is real"));
            Check.True(f.Store.ListSessions(new SessionQuery()).Any(x => x.Id == fork.Id));

            // and a non-empty fork still publishes as before (one created, one forked, zero message.added)
            var src = f.Store.CreateSession(new SessionInfo { Title = "Full" });
            f.Store.AppendMessage(src.Id, ChatMessage.UserText("one"));
            f.Store.AppendMessage(src.Id, Assistant("two"));
            await f.Bus.FlushAsync();
            lock (f.Events) f.Events.Clear();
            var fork2 = f.Store.ForkSession(src.Id, 2, new SessionInfo { Title = "Full (fork)" });
            Check.Equal(2, fork2.MessageCount);
            Check.Equal(1, (await f.EventsAsync(EventTypes.SessionCreated)).Count);
            Check.Equal(1, (await f.EventsAsync(EventTypes.SessionForked)).Count);
        });

        r.Add("sessions: deleting a project detaches its sessions — transient ones in memory too", async () =>
        {
            await using var f = new Fixture();
            var p = f.Store.CreateProject("P", T.TempDir("proj"));
            var persisted = f.Store.CreateSession(new SessionInfo { Title = "Real", ProjectId = p.Id });
            f.Store.AppendMessage(persisted.Id, ChatMessage.UserText("hi"));
            var transient = f.Store.CreateSession(new SessionInfo { Title = "Empty", ProjectId = p.Id });
            await f.Bus.FlushAsync();
            lock (f.Events) f.Events.Clear();

            f.Store.DeleteProject(p.Id);
            Check.True(f.Store.GetProject(p.Id) is null);
            Check.True(f.Store.GetSession(persisted.Id)!.ProjectId is null, "persisted session detached");
            Check.True(f.Store.GetSession(transient.Id)!.ProjectId is null, "transient session detached in memory");
            var updated = await f.EventsAsync(EventTypes.SessionUpdated);
            Check.Equal(2, updated.Count, "one session.updated per detached session");
        });
    }
}
