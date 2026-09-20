using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Provider.AiProxy;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// Regression for "no streaming — answer arrives as one lump". Two compounding
/// root causes in <see cref="AiProxyProvider"/>:
///
/// 1. <c>HttpClient.PostAsync</c> defaults to <c>ResponseFinished</c>: the whole
///    response body is buffered in memory before the response is returned, so
///    even a per-line read loop gets one in-memory lump. The streaming calls
///    must use <c>HttpCompletionOption.ResponseHeadersRead</c>.
/// 2. The SSE loops used <c>while (!reader.EndOfStream)</c>, which on a live
///    network stream can report true between packets and exit early.
///
/// The in-memory <c>StringContent</c> fakes in the other SSE tests buffer fully
/// and pass either way. These tests serve SSE over real loopback TCP via
/// <see cref="HttpListener"/> with a mid-stream gate: the server writes an
/// early SSE event, then blocks until the client has actually received and
/// emitted it. A provider that buffers the full body can never surface that
/// early delta before the server moves on, so the test fails on the bug and
/// passes on streamed I/O.
/// </summary>
public sealed class SocketStreamingTests : IAsyncLifetime
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();
    private string _prefix = "";
    private int _port;
    private TaskCompletionSource<bool>? _handshake;

    public Task InitializeAsync()
    {
        _port = FindFreePort();
        _prefix = $"http://127.0.0.1:{_port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _workers.Add(Task.Run(AcceptLoop, _cts.Token));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
        return Task.CompletedTask;
    }

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint!).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _workers.Add(Task.Run(() => HandleAsync(ctx)));
        }
    }

    private void CompleteHandshake() => _handshake?.TrySetResult(true);

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var path = req.Url!.AbsolutePath;
            using var resp = ctx.Response;

            if (path.EndsWith("/models", StringComparison.Ordinal))
            {
                SendJson(resp, "{\"data\":[{\"id\":\"m\",\"context_window\":8192,\"supports_responses\":true}]}");
                return;
            }

            if (path.EndsWith("/ack", StringComparison.Ordinal))
            {
                CompleteHandshake();
                SendJson(resp, "{}");
                return;
            }

            if (path.EndsWith("/responses", StringComparison.Ordinal))
            {
                bool isProbe = false;
                if (!string.IsNullOrEmpty(req.ContentType))
                {
                    using var sr = new StreamReader(req.InputStream);
                    var body = sr.ReadToEnd();
                    isProbe = body.Contains("\"stream\":false", StringComparison.Ordinal);
                }
                if (isProbe)
                {
                    SendJson(resp, "{\"response\":{\"id\":\"probe\"}}");
                    return;
                }
                var early = "event: response.output_text.delta\ndata: {\"delta\":\"Hello\",\"item_id\":\"m1\",\"type\":\"response.output_text.delta\"}\n\n";
                var rest =
                    "event: response.output_item.added\ndata: {\"item\":{\"id\":\"m1\",\"role\":\"assistant\",\"type\":\"message\"},\"type\":\"response.output_item.added\"}\n\n" +
                    "event: response.output_text.delta\ndata: {\"delta\":\" world\",\"item_id\":\"m1\",\"type\":\"response.output_text.delta\"}\n\n" +
                    "event: response.completed\ndata: {\"response\":{\"id\":\"r1\",\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}},\"type\":\"response.completed\"}\n\n";
                await SendSseGated(resp, early, rest);
                return;
            }

            if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                var early = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n";
                var rest =
                    "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}\n\n" +
                    "data: [DONE]\n\n";
                await SendSseGated(resp, early, rest);
                return;
            }

            resp.StatusCode = 404;
            SendJson(resp, "{}");
        }
        catch
        {
            // client went away or listener shutting down; nothing to do
        }
    }

    private static void SendJson(HttpListenerResponse resp, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        resp.StatusCode = 200;
        resp.ContentType = "application/json";
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.Close();
    }

    /// <summary>
    /// Writes an early SSE event, then blocks until the client proves it has
    /// received that event (via a separate /ack request), then streams the
    /// rest with small pauses so the client's read buffer goes empty between
    /// packets — the condition under which <c>EndOfStream</c> can lie on a
    /// real network stream. A buffering client never acks, so the gate
    /// times out and the stream is closed with the early event undelivered.
    /// </summary>
    private async Task SendSseGated(HttpListenerResponse resp, string early, string rest)
    {
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream";
        var eb = Encoding.UTF8.GetBytes(early);
        resp.OutputStream.Write(eb, 0, eb.Length);
        await resp.OutputStream.FlushAsync();

        var ack = _handshake;
        if (ack is not null && await Task.WhenAny(ack.Task, Task.Delay(8000)) != ack.Task)
        {
            try { resp.Close(); } catch { }
            return;
        }

        foreach (var line in rest.Split('\n'))
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            await resp.OutputStream.FlushAsync();
            await Task.Delay(50);
        }
        resp.Close();
    }

    private async Task<List<ModelEvent>> Collect(string wire, string stream)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_prefix) };
        var provider = new AiProxyProvider(http, _prefix, new NullLogger(), wire);
        await provider.RefreshAsync(CancellationToken.None);
        await provider.WaitForProbeAsync();
        var list = new List<ModelEvent>();
        await foreach (var e in provider.RunAsync(
            new ModelRequest
            {
                ModelId = "m",
                SessionId = "socket-" + wire + "-" + stream,
                Messages = [new AgentMessage("u", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)],
            }, CancellationToken.None))
        {
            list.Add(e);
            if (e is TextDelta)
                CompleteHandshake();
        }
        return list;
    }

    [Fact]
    public async Task ResponsesWire_StreamsDeltasOverRealSocket()
    {
        _handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ev = await Collect("responses", "gated");
        Assert.True(_handshake.Task.IsCompleted,
            "the early SSE delta was never delivered before the server closed the stream (response body was buffered)");
        Assert.Equal("Hello world", string.Concat(ev.OfType<TextDelta>().Select(d => d.Text)));
        Assert.Contains(ev, e => e is ModelCompleted);
    }

    [Fact]
    public async Task ChatWire_StreamsDeltasOverRealSocket()
    {
        _handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ev = await Collect("chat", "gated");
        Assert.True(_handshake.Task.IsCompleted,
            "the early SSE delta was never delivered before the server closed the stream (response body was buffered)");
        Assert.Equal("Hello world", string.Concat(ev.OfType<TextDelta>().Select(d => d.Text)));
        Assert.Contains(ev, e => e is ModelCompleted);
    }

    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }
}
