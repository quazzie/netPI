using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acornima;
using Acornima.Ast;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace NetPI.CodeMode;

/// <summary>
/// Runs one script: Jint on a thread of its own, the script's body inside an async function, and an event loop that hands
/// it the results of the tool calls it started. The engine is single-threaded, so everything that touches it (a host
/// function, a promise's resolution, the microtasks) runs on that thread; a tool call runs elsewhere and its result comes
/// back through <see cref="_queue"/>.
/// <para>The script sees only what <see cref="Prelude"/> builds from a few host functions that take and return strings: no
/// CLR object ever reaches it, so there is nothing to reflect on. Jint's constraints bound its memory, its recursion and,
/// through the cancellation token, its time.</para>
/// </summary>
internal sealed class ScriptRunner(ScriptHost host, ScriptLimits limits)
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly List<OutputItem> _output = [];
    private readonly List<string> _console = [];
    private readonly List<ImagePart> _images = [];
    private readonly List<Task> _calls = [];
    private int _pending;
    private long _outputChars;
    private bool? _ok;
    private string? _error;

    private sealed record OutputItem(string Text);

    /// <summary>What a run produced: the text for the model, the images, and whether the script finished.</summary>
    public sealed record Outcome(bool Ok, string Text, IReadOnlyList<ImagePart> Images, TimeSpan Elapsed, bool TimedOut);

    /// <summary>Run <paramref name="code"/>. Throws <see cref="OperationCanceledException"/> only when <paramref name="ct"/> (the run) is cancelled.</summary>
    public async Task<Outcome> RunAsync(string code, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limits.Timeout);
        // the tool calls the script started: cancelled when the script ends, whatever ended it
        using var calls = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timedOut = false;
        try
        {
            await Task.Factory.StartNew(() => Loop(code, deadline.Token, calls.Token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            _ok = false;
            _error = $"the script did not finish within {limits.Timeout.TotalSeconds:0} s";
        }
        finally
        {
            await calls.CancelAsync().ConfigureAwait(false);
            // a cancelled call ends quickly; one that ignores its token is not waited for past this
            try { await Task.WhenAll(_calls).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        ct.ThrowIfCancellationRequested();
        return new Outcome(_ok == true, Format(watch.Elapsed), _images, watch.Elapsed, timedOut);
    }

    private void Loop(string code, CancellationToken deadline, CancellationToken calls)
    {
        Engine engine = null!;
        engine = new Engine(o => o
            .LimitMemory(limits.MemoryBytes)
            .LimitRecursion(limits.Recursion)
            .CancellationToken(deadline)
            .Strict());
        engine.SetValue("__out", new Action<string>(Out));
        engine.SetValue("__console", new Action<string, string>((level, line) =>
        {
            Count(line.Length);
            lock (_console) _console.Add(level == "log" ? line : "[" + level + "] " + line);
            host.Progress(line);
        }));
        engine.SetValue("__image", new Func<string, string?>(Image));
        engine.SetValue("__done", new Action<bool, string>((ok, value) => { _ok = ok; if (ok) { if (value.Length > 0) Out(value); } else _error = value; }));
        engine.SetValue("__tools", new Func<string>(() => host.ToolsJson()));
        engine.SetValue("__search", new Func<string, int, string>((q, n) => host.SearchJson(q, n)));
        engine.SetValue("__describe", new Func<string, string>(name => host.DescribeJson(name)));
        engine.SetValue("__load", new Func<string, string>(key => host.Store.Load(key)));
        engine.SetValue("__store", new Func<string, string?, string?>((key, json) => host.Store.Set(key, json)));
        engine.SetValue("__call", new Func<string, string, JsValue>((name, args) => Call(engine, name, args, calls)));

        // the body on the function's first line, so an error's line number is the script's own
        const string Head = "(async function () {";
        Prepared<Script> script;
        try
        {
            script = Engine.PrepareScript(Head + code + "\n})().then("
                + "v => __done(true, v === undefined ? '' : typeof v === 'string' ? v : __json(v)),"
                + "e => __done(false, __error(e)));", strict: true);
        }
        catch (ScriptPreparationException pe) when (pe.InnerException is ParseErrorException ex)
        {
            // parsed before anything runs: a script that does not parse made no call
            var column = ex.LineNumber == 1 ? ex.Column - Head.Length : ex.Column;
            Fail($"the script does not parse, so nothing ran: SyntaxError: {ex.Error.Description} (line {ex.LineNumber}, column {column + 1})");
            return;
        }
        catch (Exception ex)
        {
            Fail("the script could not be prepared, so nothing ran: " + ex.Message);
            return;
        }

        try
        {
            engine.Execute(Prelude);
            engine.Execute(script);
            engine.Advanced.ProcessTasks();
            while (_ok is null)
            {
                if (_pending == 0)
                {
                    _ok = false;
                    _error = "the script waits for a promise that can never settle (no tool call is running, and there are no timers)";
                    return;
                }
                _queue.Take(deadline)();
                engine.Advanced.ProcessTasks();
            }
        }
        catch (ExecutionCanceledException) { throw new OperationCanceledException(deadline); }
        catch (OperationCanceledException) { throw; }
        catch (MemoryLimitExceededException) { Fail($"out of memory: a script may use {limits.MemoryBytes / (1024 * 1024)} MB; filter or aggregate large data instead of keeping it"); }
        catch (RecursionDepthOverflowException) { Fail("too much recursion"); }
        catch (OutputLimitException ex) { Fail(ex.Message); }
        catch (JavaScriptException ex) { Fail(ex.Error.ToString() is { Length: > 0 } s ? s : ex.Message); }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void Fail(string error)
    {
        _ok = false;
        _error = error;
    }

    /// <summary>
    /// <c>__call(name, argsJson)</c>: start a tool call and give the script a promise for its result (a JSON envelope). The
    /// call runs off the engine thread; its settlement is queued back to it.
    /// </summary>
    private JsValue Call(Engine engine, string name, string args, CancellationToken ct)
    {
        var promise = engine.Advanced.RegisterPromise();
        _pending++;
        var task = Task.Run(async () =>
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
            return await host.CallAsync(name, doc.RootElement, ct).ConfigureAwait(false);
        }, CancellationToken.None);
        lock (_calls) _calls.Add(task);
        task.ContinueWith(t => _queue.Add(() =>
        {
            _pending--;
            if (t.IsCompletedSuccessfully) promise.Resolve(t.Result);
            else promise.Reject(t.IsCanceled || t.Exception?.InnerException is OperationCanceledException
                ? "the tool call was cancelled"
                : t.Exception?.InnerException?.Message ?? "the tool call failed");
        }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return promise.Promise;
    }

    private void Out(string text)
    {
        Count(text.Length);
        lock (_output) _output.Add(new OutputItem(text));
        host.Progress(text);
    }

    /// <summary><c>image(block)</c>: an image block (<c>{ mediaType, data }</c>, base64) goes into the result; null when it is accepted, else why not.</summary>
    private string? Image(string json)
    {
        try
        {
            var o = JsonNode.Parse(json) as JsonObject;
            var media = (o?["mediaType"] ?? o?["mimeType"])?.GetValue<string>();
            var data = o?["data"]?.GetValue<string>() ?? "";
            if (media is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
                return "image() takes { mediaType: 'image/png' | 'image/jpeg' | 'image/gif' | 'image/webp', data: base64 }";
            if (data.Length == 0) return "image() needs base64 data";
            try { Convert.FromBase64String(data); } catch (FormatException) { return "image() data is not base64"; }
            if (_images.Count >= limits.MaxImages) return $"a script shows at most {limits.MaxImages} images";
            Count(data.Length);
            lock (_images) _images.Add(new ImagePart { MediaType = media, Data = data });
            return null;
        }
        catch (JsonException) { return "image() takes an image block"; }
    }

    private void Count(int chars)
    {
        if (Interlocked.Add(ref _outputChars, chars) > limits.MaxOutputChars)
            throw new OutputLimitException($"the script's output passed {limits.MaxOutputChars:N0} characters; write large data to a file with a tool and print a summary");
    }

    private sealed class OutputLimitException(string message) : Exception(message);

    /// <summary>The result text: a status line, the output items, the console lines, and the error of a failed script.</summary>
    private string Format(TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.Append(_ok == true ? "Script completed" : "Script failed").Append(" in ").Append(elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s.");
        List<OutputItem> items;
        lock (_output) items = [.. _output];
        for (var i = 0; i < items.Count; i++)
        {
            sb.Append('\n');
            if (items.Count > 1) sb.Append("==> text ").Append(i + 1).Append('/').Append(items.Count).Append(" <==\n");
            sb.Append(items[i].Text);
        }
        lock (_console)
            if (_console.Count > 0)
                sb.Append("\n<console_output>\n").Append(string.Join('\n', _console)).Append("\n</console_output>");
        if (_images.Count > 0) sb.Append('\n').Append(_images.Count).Append(_images.Count == 1 ? " image attached." : " images attached.");
        if (_ok != true) sb.Append("\nScript error: ").Append(_error ?? "unknown error");
        return sb.ToString();
    }

    /// <summary>
    /// The globals a script sees, built in JavaScript from the host functions (which take and return strings only).
    /// </summary>
    internal const string Prelude = """
        const __json = (v) => { try { const s = JSON.stringify(v); return s === undefined ? String(v) : s; } catch (e) { return String(v); } };
        const __str = (v) => typeof v === 'string' ? v : __json(v);
        const __error = (e) => {
          if (e instanceof Error) {
            const stack = typeof e.stack === 'string' ? e.stack.split('\n').filter(l => /^\s*at /.test(l)).slice(0, 4).join('\n') : '';
            return e.name + ': ' + e.message + (stack ? '\n' + stack : '');
          }
          return __str(e);
        };
        async function callTool(name, args) {
          if (typeof name !== 'string') throw new TypeError('callTool(name, args): name must be a string');
          let envelope;
          try { envelope = await __call(name, JSON.stringify(args ?? {})); }
          catch (e) { throw new Error(name + ': ' + __str(e)); }
          return JSON.parse(envelope);
        }
        const tools = new Proxy({}, {
          get(_, name) {
            if (typeof name !== 'string') return undefined;
            return async (args) => {
              const r = await callTool(name, args);
              if (r.isError) throw new Error(name + ': ' + r.content);
              return r.content;
            };
          },
          has(_, name) { return ALL_TOOLS.some(t => t.name === name); },
          ownKeys() { return ALL_TOOLS.map(t => t.name); },
          getOwnPropertyDescriptor(_, name) { return ALL_TOOLS.some(t => t.name === name) ? { enumerable: true, configurable: true } : undefined; },
        });
        const ALL_TOOLS = Object.freeze(JSON.parse(__tools()));
        const searchTools = (query, options) => JSON.parse(__search(String(query ?? ''), Number((options && options.limit) || 8)));
        const describeTool = (name) => {
          const s = __describe(String(name));
          if (!s) throw new Error("describeTool: unknown tool '" + String(name) + "'; find tools with searchTools(query) or ALL_TOOLS");
          return JSON.parse(s);
        };
        const text = (v) => { __out(__str(v)); };
        const image = (block) => {
          const why = __image(__json(block && block.images && block.images[0] ? block.images[0] : block));
          if (why) throw new TypeError(why);
        };
        const store = (key, value) => {
          const why = __store(String(key), value === undefined ? null : __json(value));
          if (why) throw new RangeError(why);
        };
        const load = (key) => { const s = __load(String(key)); return s ? JSON.parse(s) : undefined; };
        const console = Object.freeze({
          log: (...a) => __console('log', a.map(__str).join(' ')),
          info: (...a) => __console('info', a.map(__str).join(' ')),
          warn: (...a) => __console('warn', a.map(__str).join(' ')),
          error: (...a) => __console('error', a.map(__str).join(' ')),
          debug: (...a) => __console('debug', a.map(__str).join(' ')),
        });
        """;
}

/// <summary>The bounds of one script.</summary>
internal sealed record ScriptLimits(TimeSpan Timeout, long MemoryBytes, int Recursion = 1000, long MaxOutputChars = 4_000_000, int MaxImages = 8);
