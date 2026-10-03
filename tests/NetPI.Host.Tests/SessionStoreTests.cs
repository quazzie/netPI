using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Settings;
using NetPI.Host.Storage.Sqlite;
using NetPI.Host.Events;
using NetPI.Host.Sessions;

namespace NetPI.Host.Tests;

public static class SessionStoreTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Dir = T.TempDir("sessions");
        public readonly IStorage Storage;
        /// <summary>The SQL engine behind the storage, for tests that stage or inspect rows directly.</summary>
        public readonly Database Db;
        public readonly EventBus Bus = new(NullLogger.Instance);
        public readonly SessionService Store;
        public readonly List<BusEvent> Events = [];
        private readonly IDisposable _sub;

        public Fixture()
        {
            Storage = Open(Dir);
            Db = ((SqliteStorage)Storage).Database;
            Store = new SessionService(Storage, Bus, Path.Combine(Dir, "workspace"));
            _sub = Bus.Subscribe("*", e => { lock (Events) Events.Add(e); });
        }

        /// <summary>The sqlite storage of a home folder (a second one over the same folder is a simulated restart).</summary>
        public static IStorage Open(string dir)
        {
            Directory.CreateDirectory(dir);
            return new SqliteStorageProvider().Open(new StorageOpenOptions
            {
                Home = dir, Logger = NullLogger.Instance, Settings = new SettingsStore(Path.Combine(dir, "settings.json"), NullLogger.Instance),
            });
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
            Storage.Dispose();
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
            Check.Equal(SessionService.DefaultTitle, s.Title);
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
            Check.Equal(SessionService.DefaultTitle, f.Store.GetSession(s.Id)!.Title, "notices do not title");
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

        r.Add("sessions: sent-prefix history survives a store restart and trims historical forks", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("first"));
            f.Store.UpdateSession(s.Id, x => SessionPrompt.RecordSent(x, "prefix A", 0, 1));
            f.Store.AppendMessage(s.Id, Assistant("first response"));
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("second"));
            f.Store.UpdateSession(s.Id, x => { SessionPrompt.Invalidate(x); SessionPrompt.RecordSent(x, "prefix B", 1, 3); });
            using var freshStorage = Fixture.Open(f.Dir);
            var fresh = new SessionService(freshStorage, f.Bus, Path.Combine(f.Dir, "workspace"));
            var current = fresh.GetSession(s.Id)!;
            var early = fresh.ForkSession(s.Id, 2, SessionFork.Template(current, 2, 0, []));
            Check.Equal("prefix A", SessionPrompt.Fallback(fresh.GetSession(early.Id)!));
            Check.Equal(1, ((JsonArray)early.Meta![SessionPrompt.HistoryKey]!).Count);
            Check.Equal("prefix B", SessionPrompt.Fallback(current), "original unchanged");
            var before = SessionFork.Template(current, 0, 0, []);
            Check.True(SessionPrompt.Fallback(before) is null);
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
            var runState = new[] { "goal", "todo", "budgetAllowedFrom", "guardrailsAllowed" };   // what plugins declare as run state
            var t = SessionFork.Template(from, 7, 1234, runState, new HashSet<string> { "Chat", "Chat (fork)" });
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

        r.Add("sessions: declared run-state keys are dropped at a fork and remembered across a restart; meta.cwd is never copied", async () =>
        {
            await using var f = new Fixture();
            f.Store.DeclareForkReset("goal", "todo");
            var s = f.Store.CreateSession(new SessionInfo
            {
                Meta = new JsonObject { ["goal"] = new JsonObject { ["status"] = "active" }, ["todo"] = new JsonArray(), ["profile"] = "coder", [SessionCwd.MetaKey] = "C:/checkout" },
            });
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("hello"));
            Check.Equal("C:/checkout", f.Store.GetCwd(f.Store.GetSession(s.Id)!), "the folder a session carries is where it runs");

            // A store started later, in a run where the plugin that declared the keys is not loaded: the keys are still known.
            using var restarted = Fixture.Open(f.Dir);
            var fresh = new SessionService(restarted, f.Bus, Path.Combine(f.Dir, "workspace"));
            var current = fresh.GetSession(s.Id)!;
            var fork = fresh.ForkSession(s.Id, 1, SessionFork.Template(current, 1, 0, fresh.ForkResetKeys()));
            var meta = fresh.GetSession(fork.Id)!.Meta!;
            Check.True(!meta.ContainsKey("goal") && !meta.ContainsKey("todo"), "run state a plugin declared does not reach a fork, even with that plugin absent");
            Check.True(!meta.ContainsKey(SessionCwd.MetaKey), "a fork starts in its project's folder, not its parent's checkout");
            Check.Equal("coder", meta["profile"]!.GetValue<string>(), "setup is kept");
            await Task.CompletedTask;
        });

        r.Add("sessions: a plugin finds the sessions it attached something to, including ones with no message yet", async () =>
        {
            await using var f = new Fixture();
            var stored = f.Store.CreateSession(new SessionInfo { Meta = new JsonObject { ["workspaceId"] = "wsp_1" } });
            f.Store.AppendMessage(stored.Id, ChatMessage.UserText("hello"));
            var archived = f.Store.CreateSession(new SessionInfo { Meta = new JsonObject { ["workspaceId"] = "wsp_1" } });
            f.Store.AppendMessage(archived.Id, ChatMessage.UserText("hello"));
            f.Store.UpdateSession(archived.Id, x => x.Archived = true);
            var transient = f.Store.CreateSession(new SessionInfo { Meta = new JsonObject { ["workspaceId"] = "wsp_1" } });
            f.Store.CreateSession(new SessionInfo { Meta = new JsonObject { ["workspaceId"] = "wsp_2" } });

            List<string> Find(bool archivedToo, bool unmaterialized) => f.Store.ListSessions(new SessionQuery
            {
                AttachedKey = "workspaceId", AttachedValue = "wsp_1", IncludeArchived = archivedToo, IncludeUnmaterialized = unmaterialized,
            }).Select(x => x.Id).Order().ToList();

            Check.Equal(string.Join(",", new[] { stored.Id }.Order()), string.Join(",", Find(false, false)), "stored and not archived");
            Check.Equal(string.Join(",", new[] { stored.Id, archived.Id }.Order()), string.Join(",", Find(true, false)), "archived ones when asked");
            Check.Equal(string.Join(",", new[] { stored.Id, transient.Id }.Order()), string.Join(",", Find(false, true)), "a session with no message yet is found when asked");
            await Task.CompletedTask;
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
        r.Add("sessions: a first message materializing a session never deadlocks with a thread inside a transaction reading a session", async () =>
        {
            // The first message of a session (it lives in memory until then) takes the transient-session lock and then the database
            // gate; a transaction holds the gate and reads a session, which looks the transient ones up under that lock. Two threads
            // in opposite order froze every database call of the process (a subagent's first message racing any other append did
            // it: /api/health still answered, nothing that reads the database did, CPU idle). One lock now: no order to get wrong.
            // not disposed when the deadlock is found: closing the database would wait for the gate the deadlocked threads hold
            var f = new Fixture();
            var fresh = f.Store.CreateSession(new SessionInfo { Title = "fresh" });               // transient: no messages yet
            var stored = f.Store.CreateSession(new SessionInfo { Title = "stored" });
            f.Store.AppendMessage(stored.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "hello" }] });

            Exception? materializeFailed = null;
            var materializer = new Thread(() =>
            {
                try { f.Store.AppendMessage(fresh.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "first" }] }); }
                catch (Exception ex) { materializeFailed = ex; }
            }) { IsBackground = true };

            var holder = Task.Run(() => f.Storage.Sessions.Atomic(_ =>
            {
                // this thread now holds the database gate; start the materialization and wait until it is blocked behind us
                materializer.Start();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while ((materializer.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);
                // reading a session inside the transaction, as every update and append does
                var read = f.Store.GetSession(stored.Id);
                Check.Equal("stored", read?.Title, "the stored session is read");
                return 0;
            }));

            var finished = await Task.WhenAny(holder, Task.Delay(TimeSpan.FromSeconds(10))) == holder;
            Check.True(finished, "the transaction finished: a thread inside it reading a session did not wait for a lock the materializing thread holds");
            await holder;
            Check.True(materializer.Join(TimeSpan.FromSeconds(10)), "the first message was stored once the transaction ended");
            Check.True(materializeFailed is null, "no failure: " + materializeFailed);
            Check.Equal(1, f.Store.GetSession(fresh.Id)?.MessageCount, "the session is stored with its first message");
            await f.DisposeAsync();
        });

        r.Add("sessions: the whole life of sessions on many threads at once never freezes the store", async () =>
        {
            // create (in memory), update, give a project, first message (stored), appends, update again, read, list, delete: every
            // public path that takes the transient-session state or the database, on eight threads. A lock taken in two orders
            // anywhere in the store ends this in a freeze, which is reported rather than waited for.
            var f = new Fixture();
            var project = f.Store.CreateProject("stress", T.TempDir("stress-project"));
            const int workers = 8, rounds = 40;
            var errors = new List<string>();
            var threads = Enumerable.Range(0, workers).Select(w => new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < rounds; i++)
                    {
                        var s = f.Store.CreateSession(new SessionInfo { Title = $"w{w}-{i}", Kind = i % 3 == 0 ? "subagent" : "chat" });
                        f.Store.UpdateSession(s.Id, x => x.Meta = new JsonObject { ["n"] = i });          // transient: in memory
                        f.Store.SetSessionProject(s.Id, project.Id);
                        f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "first " + i }] });
                        f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "answer" }] });
                        f.Store.UpdateSession(s.Id, x => x.Title = $"renamed {w}-{i}");                  // stored: in a transaction
                        f.Store.GetContextMessages(s.Id);
                        f.Store.ListSessions(new SessionQuery { Limit = 5 });
                        f.Store.SetSessionProject(s.Id, null);
                        if (i % 4 == 0) f.Store.DeleteSession(s.Id);
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add($"worker {w}: {ex.GetType().Name}: {ex.Message}"); }
            }) { IsBackground = true }).ToList();
            foreach (var t in threads) t.Start();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            var stuck = threads.Where(t => !t.Join(TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds)))).Count();
            Check.Equal(0, stuck, $"{stuck} of {workers} threads are frozen: a lock is taken in two orders");
            Check.Equal(0, errors.Count, string.Join(" | ", errors));
            Check.Equal(workers * rounds - workers * (rounds / 4), f.Store.ListSessions(new SessionQuery { Limit = 1000, IncludeSubagents = true }).Count, "every session that was not deleted is there");
            await f.DisposeAsync();
        });

        r.Add("sessions: a read that races an append never caches a view without it", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            // Big enough that a read (a query plus a parse of the whole history) is still running when the append lands.
            var text = string.Join("\n", Enumerable.Range(0, 40).Select(i => new string((char)('a' + i % 26), 80)));
            for (var i = 0; i < 1200; i++) f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = $"m{i} {text}" }] });

            // A reader that started before the append may (correctly) miss it; a reader that starts after must not, and
            // no later read may serve a cache that is missing a message the database already has. Whether the race is hit
            // depends on the timing - the guard is the second read (MAX(seq)) in ContextRows - so the assertion is the
            // invariant, which holds either way.
            var readers = 4;
            var started = new TaskCompletionSource();
            var reading = Enumerable.Range(0, readers).Select(_ => Task.Run(() =>
            {
                started.Task.Wait();
                return f.Store.GetContextMessages(s.Id).Count;
            })).ToArray();
            started.SetResult();
            await Task.Delay(5);
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("the one that raced"));
            var counts = await Task.WhenAll(reading);

            Check.Equal(1201, f.Store.GetContextMessages(s.Id).Count, "the read after the append sees every message");
            Check.True(counts.All(c => c is 1200 or 1201), "a reader that raced sees a view from before or after the append, never a torn one: " + string.Join(",", counts));
            var reads = f.Store.ContextCache.Reads;
            Check.Equal(1201, f.Store.GetContextMessages(s.Id).Count);
            Check.True(f.Store.ContextCache.Reads >= reads, "and the next read is served from the cache or re-read, never a stale fill");
        });

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

        r.Add("sessions: a pinned session stays at the top of the list and inside its newest-first window", async () =>
        {
            await using var f = new Fixture();
            // Pinned before its first message: the pin rides into the row at materialization
            var ancient = f.Store.CreateSession(new SessionInfo { Title = "Ancient" });
            f.Store.UpdateSession(ancient.Id, x => x.Pinned = true);
            f.Store.AppendMessage(ancient.Id, ChatMessage.UserText("old news"));
            // Timestamps store at millisecond resolution: on a fast machine the two pinned sessions' last
            // writes can land in one millisecond, and the ordering would fall to the id tiebreak instead of
            // newest-first. Separate them so the assertion tests the ordering, not the clock.
            await Task.Delay(2);

            var old = f.Store.CreateSession(new SessionInfo { Title = "Old chat" });
            f.Store.AppendMessage(old.Id, ChatMessage.UserText("hello"));
            f.Store.UpdateSession(old.Id, x => x.Pinned = true);

            for (var i = 1; i <= 29; i++)
            {
                var s = f.Store.CreateSession(new SessionInfo { Title = $"Newer {i:D2}" });
                f.Store.AppendMessage(s.Id, ChatMessage.UserText("m" + i));
            }

            Check.True(f.Store.GetSession(ancient.Id)!.Pinned, "a pin set while transient survives materialization");
            Check.True(f.Store.GetSession(old.Id)!.Pinned, "pinned round-trips through the store");
            var list = f.Store.ListSessions(new SessionQuery { Limit = 5000 });
            Check.Equal(31, list.Count);
            Check.Equal(old.Id, list[0].Id, "pinned first, newest first within the group");
            Check.Equal(ancient.Id, list[1].Id, "the older pinned one after it");
            Check.True(list.Skip(2).All(s => !s.Pinned));
            Check.True(list.Skip(2).Zip(list.Skip(3)).All(p => p.First.UpdatedAt >= p.Second.UpdatedAt), "newest first overall after the pinned prefix");

            // The point of the ordering: a small newest-first window still holds the pinned sessions
            var page = f.Store.ListSessions(new SessionQuery { Limit = 10 });
            Check.Equal(2, page.Count(s => s.Pinned), "both pinned fit the first page");
            var second = f.Store.ListSessions(new SessionQuery { Limit = 10, Offset = 10 });
            Check.Equal(10, second.Count);
            Check.True(second.All(s => !s.Pinned), "pinned rows only live on the first page");
        });

        r.Add("sessions: pinning is a session field — session.updated, not session.changed", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("hello"));
            await f.Bus.FlushAsync();
            lock (f.Events) f.Events.Clear();

            f.Store.UpdateSession(s.Id, x => x.Pinned = true);
            Check.Equal(1, (await f.EventsAsync(EventTypes.SessionUpdated)).Count);
            Check.Equal(0, (await f.EventsAsync(EventTypes.SessionChanged)).Count, "pinned is a column, not a meta key");
            lock (f.Events) f.Events.Clear();

            f.Store.UpdateSession(s.Id, x => x.Pinned = false);
            Check.Equal(1, (await f.EventsAsync(EventTypes.SessionUpdated)).Count);
            Check.Equal(0, (await f.EventsAsync(EventTypes.SessionChanged)).Count);
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
            Check.True(f.Store.GetSession(s.Id) is { Title: SessionService.DefaultTitle });
            Check.Equal(0, f.Store.ListSessions(new SessionQuery()).Count, "not listed while empty");
            Check.Equal(0, (await f.EventsAsync(EventTypes.SessionCreated)).Count, "not announced");

            // a fresh store over the same database (a simulated restart) does not know it
            var freshDb = Fixture.Open(f.Dir);
            var freshStore = new SessionService(freshDb, f.Bus, Path.Combine(f.Dir, "workspace"));
            Check.True(freshStore.GetSession(s.Id) is null, "no row in the database");
            freshDb.Dispose();

            // first message: materialized — row, title, count, and the events in order
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("the first words\nand more"));
            Check.True(f.Store.ListSessions(new SessionQuery()).Any(x => x.Id == s.Id), "listed once it has a message");
            Check.Equal("the first words", f.Store.GetSession(s.Id)!.Title, "auto-title at materialization");
            Check.Equal(1L, f.Store.GetSession(s.Id)!.MessageCount);
            var freshDb2 = Fixture.Open(f.Dir);
            var freshStore2 = new SessionService(freshDb2, f.Bus, Path.Combine(f.Dir, "workspace"));
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

        r.Add("sessions: deleting a project publishes sessions that are already detached, with the new stamp", async () =>
        {
            await using var f = new Fixture();
            var p = f.Store.CreateProject("P", T.TempDir("proj"));
            var s = f.Store.CreateSession(new SessionInfo { Title = "Real", ProjectId = p.Id });
            f.Store.AppendMessage(s.Id, ChatMessage.UserText("hi"));
            var before = f.Store.GetSession(s.Id)!.UpdatedAt;
            await f.Bus.FlushAsync();
            lock (f.Events) f.Events.Clear();
            // Stamps have millisecond resolution: let the clock pass the session's own, so a bump is observable at all.
            await Wait.Until(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > before.ToUnixTimeMilliseconds(), "the clock to pass the session's stamp");

            f.Store.DeleteProject(p.Id);

            var updated = await f.EventsAsync(EventTypes.SessionUpdated);
            Check.Equal(1, updated.Count, "one session.updated for the detached session");
            var payload = JsonSerializer.SerializeToNode(updated[0].Data, NetPiJson.Options)!["session"]!;
            Check.True(payload["projectId"] is null, "the payload does not carry the deleted project id: " + payload.ToJsonString());
            var published = payload["updatedAt"]!.GetValue<DateTimeOffset>();
            Check.True(published.ToUnixTimeMilliseconds() > before.ToUnixTimeMilliseconds(), $"its stamp moved on ({published:O} after {before:O})");
            Check.Equal(f.Store.GetSession(s.Id)!.UpdatedAt.ToUnixTimeMilliseconds(), published.ToUnixTimeMilliseconds(), "and it is the stamp the row carries now");
        });

        r.Add("sessions: a corrupted stored row is a data error naming the row, not a bad request", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            var m = f.Store.AppendMessage(s.Id, Assistant("hello"));
            // Corrupt the parts of one row: the chat must fail saying WHICH row is broken and the host must log it,
            // not blame the caller with "Invalid parameters" and leave no trace.
            f.Db.Execute("UPDATE messages SET parts = 'not json{{' WHERE id = @id", new { id = m.Id });
            var ex = Check.Throws<InvalidDataException>(() => f.Store.GetMessages(s.Id));
            Check.Contains(ex.Message, m.Id.ToString());
            Check.Contains(ex.Message, "corrupted");
        });

        r.Add("sessions: an update racing context reads leaves no pre-update rows in the context cache", async () =>
        {
            await using var f = new Fixture();
            var s = f.Store.CreateSession(new SessionInfo());
            var m = f.Store.AppendMessage(s.Id, Assistant("v0"));

            // The race the cache must survive: a read that starts while an update commits in between its rows and
            // its staleness check. Without the per-session generation, that read re-fills the slot with the old row
            // after the drop, and the stale entry outlives the write. The writer's check right after each update is
            // what the fixed cache must never fail: it may only see a version the writer has already committed.
            using var stopper = new CancellationTokenSource();
            var errors = new List<Exception>();
            var reader = Task.Run(() =>
            {
                try
                {
                    while (!stopper.IsCancellationRequested)
                    {
                        var rows = f.Store.GetContextMessages(s.Id);
                        if (rows.Count != 1 || rows[0].Parts.Count == 0) throw new AssertException($"context read came back odd ({rows.Count} rows)");
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            });
            try
            {
                for (var i = 1; i <= 100; i++)
                {
                    f.Store.UpdateMessage(new ChatMessage
                    {
                        Id = m.Id, SessionId = s.Id, Seq = m.Seq, Role = MessageRole.Assistant,
                        Parts = [new TextPart { Text = "v" + i }],
                    });
                    Check.Equal("v" + i, f.Store.GetContextMessages(s.Id).Single().Text, $"context after update {i}");
                }
            }
            finally
            {
                stopper.Cancel();
                reader.GetAwaiter().GetResult();
            }
            lock (errors)
                Check.True(errors.Count == 0, "the racing reads failed: " + (errors.FirstOrDefault()?.Message ?? ""));
            Check.Equal("v100", f.Store.GetContextMessages(s.Id).Single().Text, "the settled cache holds the last write");
        });

        r.Add("sessions: abandoned empty chats are evicted once the cap is passed", async () =>
        {
            await using var f = new Fixture();
            var created = new List<SessionInfo>();
            for (var i = 0; i < 101; i++) created.Add(f.Store.CreateSession(new SessionInfo { Title = "n" + i }));

            // The cap (MaxTransientSessions) is below 101: one abandoned empty chat has been evicted, with the
            // event that keeps a UI's list from ghosting it.
            var listed = f.Store.ListSessions(new SessionQuery { IncludeUnmaterialized = true });
            Check.Equal(100, listed.Count);
            var deleted = await f.EventsAsync(EventTypes.SessionDeleted);
            Check.Equal(1, deleted.Count, "one session.deleted for the evicted chat");
            var evictedId = JsonSerializer.SerializeToNode(deleted[0].Data, NetPiJson.Options)!["id"]!.GetValue<string>();
            Check.True(created.Any(s => s.Id == evictedId), "the event names one of the created chats");
            Check.True(f.Store.GetSession(evictedId) is null, "the evicted chat is gone");

            // A chat that got its first message is materialized and unaffected by the cap.
            var kept = f.Store.CreateSession(new SessionInfo { Title = "kept" });
            f.Store.AppendMessage(kept.Id, ChatMessage.UserText("hi"));
            Check.True(f.Store.GetSession(kept.Id) is not null, "a materialized chat survives");
            Check.Equal(100, f.Store.ListSessions(new SessionQuery { IncludeUnmaterialized = true }).Count, "99 empty transient + 1 materialized");
        });

        r.Add("host: the home is resolved the same way by server and desktop (option, env var, ~ expansion)", () =>
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // The desktop used to read NETPI_HOME without expansion, so NETPI_HOME=~/x split its window.json and
            // WebView profile from the server's home. It now shares the server's resolution, which expands ~.
            Check.Equal(PathUtil.Normalize(Path.Combine(profile, "netpi-home")), HostKernel.ResolveHome("~/netpi-home"));
            Check.Equal(PathUtil.Normalize(Path.Combine(profile, ".netpi")), HostKernel.ResolveHome(null) /* default, env unset below */);

            var old = Environment.GetEnvironmentVariable("NETPI_HOME");
            try
            {
                Environment.SetEnvironmentVariable("NETPI_HOME", "~/netpi-env");
                Check.Equal(PathUtil.Normalize(Path.Combine(profile, "netpi-env")), HostKernel.ResolveHome(null));
                Check.Equal(PathUtil.Normalize(Path.Combine(profile, "from-arg")), HostKernel.ResolveHome("~/from-arg"), "the option wins over the env var");
            }
            finally { Environment.SetEnvironmentVariable("NETPI_HOME", old); }
        });
    }
}
