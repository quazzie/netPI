using NetPI.Web;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 §11a (F/P5): the Web listener binds loopback, but loopback binding is
/// NOT browser-origin validation — a malicious page in ANY browser can open a
/// cross-origin request to 127.0.0.1:port. The control surface (/ws) and the
/// mutating shell action (/api/open) therefore validate Host/Origin. These are the
/// two pure predicates at the core of that gate.
/// </summary>
public sealed class ControlBoundaryTests
{
    private const int Port = 5173;

    // ---- ControlOriginIsAllowed (WS upgrade + /api/open gate) ----------------

    [Theory]
    [InlineData("127.0.0.1", null)]                       // loopback host, no origin (CLI / originless)
    [InlineData("127.0.0.1:5173", null)]
    [InlineData("localhost", null)]
    [InlineData("127.0.0.1", "http://127.0.0.1:5173")]    // same-origin app
    [InlineData("localhost:5173", "http://localhost:5173")]
    [InlineData("127.0.0.1", "http://127.0.0.1:5173/")]   // trailing path is fine (port/host matter)
    public void ControlOrigin_AllowsLoopbackAndSameOrigin(string host, string? origin)
        => Assert.True(NetPI.Web.WebApp.ControlOriginIsAllowed(host, origin, Port));

    [Theory]
    [InlineData("evil.com", "http://127.0.0.1:5173")]      // host not loopback
    [InlineData("127.0.0.1", "http://evil.com")]           // wrong origin (cross-origin page)
    [InlineData("127.0.0.1", "http://127.0.0.1:5174")]     // wrong port
    [InlineData("127.0.0.1", "https://127.0.0.1:5173")]    // wrong scheme (not http)
    [InlineData("127.0.0.1", "not a url")]                 // unparseable origin
    [InlineData("127.0.0.1", "file://")]
    public void ControlOrigin_RejectsNonLoopbackAndWrongOrigin(string host, string? origin)
        => Assert.False(NetPI.Web.WebApp.ControlOriginIsAllowed(host, origin, Port));

    [Fact]
    public void ControlOrigin_MissingHost_IsRejected()
        => Assert.False(NetPI.Web.WebApp.ControlOriginIsAllowed(null, null, Port));

    // ---- IsActiveContent (/api/file active-content protection) ---------------

    [Theory]
    [InlineData("/x/y.html")]
    [InlineData("/x/y.HTM")]
    [InlineData("/x/y.svg")]
    [InlineData("/x/y.mhtml")]
    public void IsActiveContent_FlagsHtmlAndSvg(string p)
        => Assert.True(NetPI.Web.WebApp.IsActiveContent(p));

    [Theory]
    [InlineData("/x/y.txt")]
    [InlineData("/x/y.json")]
    [InlineData("/x/y.cs")]
    [InlineData("/x/noext")]
    [InlineData("/x/y.png")]
    public void IsActiveContent_IgnoresInertFiles(string p)
        => Assert.False(NetPI.Web.WebApp.IsActiveContent(p));
}
