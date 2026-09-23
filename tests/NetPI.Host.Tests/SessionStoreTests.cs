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

        r.Add("sessions: project attach/detach appends a project notice; cwd follows", async () =>
        {
            await using var f = new Fixture();
            var projDir = T.TempDir("proj");
            var p = f.Store.CreateProject("", projDir);
            Check.Equal(Path.GetFileName(projDir), p.Name);
            var s = f.Store.CreateSession(new SessionInfo());
            Check.Equal(Path.Combine(f.Dir, "workspace"), f.Store.GetCwd(s));

            var attached = f.Store.SetSessionProject(s.Id, p.Id);
            Check.Equal(p.Id, attached.ProjectId);
            Check.Equal(p.Path, f.Store.GetCwd(attached));
            var notice = f.Store.GetMessages(s.Id).Last();
            Check.Equal(MessageRole.Notice, notice.Role);
            Check.Equal("project", notice.MetaString("kind"));
            Check.Equal($"The workspace changed: this session is now attached to project \"{p.Name}\" at {p.Path}. Your working directory is now {p.Path} — resolve relative paths against it.", notice.Text);
            Check.Equal(1L, attached.MessageCount);

            var same = f.Store.SetSessionProject(s.Id, p.Id);
            Check.Equal(1, f.Store.GetMessages(s.Id).Count, "no notice when nothing changes");

            var detached = f.Store.SetSessionProject(s.Id, null);
            Check.Equal<string?>(null, detached.ProjectId);
            var n2 = f.Store.GetMessages(s.Id).Last();
            Check.Contains(n2.Text, "no longer attached to a project");
            Check.Contains(n2.Text, Path.Combine(f.Dir, "workspace"));
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

        r.Add("sessions: update mutates, persists and publishes session.updated", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo { Model = "aiproxy/a", Meta = new JsonObject { ["x"] = 1 } });
            var u = f.Store.UpdateSession(s.Id, x => { x.Model = "anthropic/b"; x.Reasoning = "high"; x.ContextTokens = 1234; x.Title = "Renamed"; });
            var back = f.Store.GetSession(s.Id)!;
            Check.Equal("anthropic/b", back.Model);
            Check.Equal("high", back.Reasoning);
            Check.Equal(1234L, back.ContextTokens);
            Check.Equal("Renamed", back.Title);
            Check.Equal(1, back.Meta!["x"]!.GetValue<int>());
            Check.True(u.UpdatedAt >= s.UpdatedAt);
            Check.Throws<KeyNotFoundException>(() => f.Store.UpdateSession("ses_missing", _ => { }));
            var evs = await f.EventsAsync(EventTypes.SessionUpdated);
            Check.Equal(1, evs.Count);
            Check.Equal(1, (await f.EventsAsync(EventTypes.SessionCreated)).Count);
        });
    }
}
