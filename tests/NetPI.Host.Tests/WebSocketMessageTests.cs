using System.Net.WebSockets;
using NetPI.Web;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 §11a (F): the WebSocket receive path must assemble COMPLETE messages
/// (a single ReceiveAsync call is not one message), bound total message size, and
/// decode UTF-8 only across complete bytes. The pure accumulator is exercised
/// directly (frames injected), so no real socket is needed.
/// </summary>
public sealed class WebSocketMessageTests
{
    private static readonly CancellationToken NoCt = CancellationToken.None;

    /// <summary>Feeds frames one at a time via the injected receive delegate.</summary>
    private static Task<string?> Accumulate(params NetPI.Web.WebApp.WsFrame[] frames)
    {
        var i = 0;
        Task<NetPI.Web.WebApp.WsFrame?> Next(CancellationToken ct)
        {
            // null (no frame) signals a clean close; otherwise the next frame.
            NetPI.Web.WebApp.WsFrame? f = i < frames.Length ? frames[i++] : null;
            return Task.FromResult(f);
        }
        return NetPI.Web.WebApp.AccumulateMessageAsync(Next, maxBytes: 1024 * 1024, NoCt);
    }

    [Fact]
    public async Task SingleFrame_ReturnsItsText()
    {
        var text = "{\"type\":\"chat.send\",\"payload\":{}}";
        var result = await Accumulate(new NetPI.Web.WebApp.WsFrame(
            System.Text.Encoding.UTF8.GetBytes(text), EndOfMessage: true));
        Assert.Equal(text, result);
    }

    [Fact]
    public async Task FragmentedMessage_IsAssembledFromMultipleFrames()
    {
        // A long prompt split into three partial frames; EndOfMessage only on the last.
        var a = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"chat.send\",\"payload\":{\"text\":\"1234567890");
        var b = System.Text.Encoding.UTF8.GetBytes("12345678901234567890\"}");
        var result = await Accumulate(
            new NetPI.Web.WebApp.WsFrame(a, EndOfMessage: false),
            new NetPI.Web.WebApp.WsFrame(new byte[0], EndOfMessage: false), // an empty mid-frame
            new NetPI.Web.WebApp.WsFrame(b, EndOfMessage: true));
        Assert.Equal("{\"type\":\"chat.send\",\"payload\":{\"text\":\"123456789012345678901234567890\"}", result);
    }

    [Fact]
    public async Task MultibyteUtf8SplitAcrossFrames_DecodesAsOneSequence()
    {
        // The 4-byte '😀' is split across frames; it must decode to the exact code point.
        var full = System.Text.Encoding.UTF8.GetBytes("A😀B");
        // split: [A😀 first 3 bytes] + [4th byte, B]
        var f1 = new byte[3];
        var f2 = new byte[full.Length - 3];
        System.Array.Copy(full, 0, f1, 0, 3);
        System.Array.Copy(full, 3, f2, 0, f2.Length);
        var result = await Accumulate(
            new NetPI.Web.WebApp.WsFrame(f1, EndOfMessage: false),
            new NetPI.Web.WebApp.WsFrame(f2, EndOfMessage: true));
        Assert.Equal("A😀B", result);
    }

    [Fact]
    public async Task OversizedMessage_ExceedingTheBound_Throws()
    {
        // Two frames whose TOTAL exceeds the bound → the accumulator throws.
        var i = 0;
        Task<NetPI.Web.WebApp.WsFrame?> Next(CancellationToken ct)
        {
            NetPI.Web.WebApp.WsFrame? f = i++ == 0
                ? new NetPI.Web.WebApp.WsFrame(new byte[600], EndOfMessage: false)
                : new NetPI.Web.WebApp.WsFrame(new byte[600], EndOfMessage: true);
            return Task.FromResult(f);
        }
        var ex = await Assert.ThrowsAsync<WebSocketException>(async () =>
            await NetPI.Web.WebApp.AccumulateMessageAsync(Next, maxBytes: 1000, NoCt));
        Assert.Contains("exceeds", ex.Message);
    }

    [Fact]
    public async Task CloseFrame_ReturnsNull()
    {
        // A null frame from the delegate signals a clean close.
        var result = await Accumulate(); // zero frames → immediate close
        Assert.Null(result);
    }
}
