using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NetPI.Tools.Media;

namespace NetPI.Aux.Tests;

public static class MediaTests
{
    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private sealed class Env
    {
        public FakePluginContext Ctx { get; } = new(T.TempDir("media-home"));
        public string Dir { get; } = T.TempDir("media");

        public async Task StartAsync() => await new MediaPlugin().StartAsync(Ctx, CancellationToken.None);

        public Task<ToolResult> Run(object args) =>
            Ctx.ToolsFake.Get("show_image")!.ExecuteAsync(new ToolContext
            {
                SessionId = "ses_1", AgentId = "agt_1", CallId = "call_1", Cwd = Dir, Services = Ctx.Services, Events = Ctx.Events,
            }, T.Args(args), CancellationToken.None);
    }

    private static JsonElement D(ToolResult r) => NetPiJson.ToElement(r.Details);

    public static void Register(TestRunner r)
    {
        r.Add("show_image: files, URLs and data: URLs reach the UI with a caption; the model only gets a line", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var tool = env.Ctx.ToolsFake.Get("show_image")!;
            Check.True(tool.Definition is { Category: "media", ReadOnly: true, SummaryArg: "source" });

            var png = Convert.FromBase64String(Png1x1);
            File.WriteAllBytes(Path.Combine(env.Dir, "chart.png"), png);
            var res = await env.Run(new { source = "chart.png", caption = "Build times per day" });
            Check.False(res.IsError, res.Content);
            Check.Equal("Showed chart.png (image/png, 1 KB) to the user with the caption \"Build times per day\".", res.Content);
            Check.True(res.Images is null or { Count: 0 }, "not sent to the model");
            var d = D(res);
            Check.Equal("file", d.GetProperty("source").GetString());
            Check.Equal(Path.Combine(env.Dir, "chart.png"), d.GetProperty("path").GetString());
            Check.Equal("image/png", d.GetProperty("mediaType").GetString());
            Check.Equal(Png1x1, d.GetProperty("data").GetString());
            Check.Equal("Build times per day", d.GetProperty("caption").GetString());

            // the type comes from the bytes: a PNG saved as .dat is still a PNG
            File.WriteAllBytes(Path.Combine(env.Dir, "odd.dat"), png);
            Check.Equal("image/png", D(await env.Run(new { path = "odd.dat" })).GetProperty("mediaType").GetString());

            File.WriteAllText(Path.Combine(env.Dir, "logo.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"8\" height=\"8\"><rect width=\"8\" height=\"8\"/></svg>");
            Check.Equal("image/svg+xml", D(await env.Run(new { file = "logo.svg" })).GetProperty("mediaType").GetString());

            var data = await env.Run(new { source = "data:image/png;base64," + Png1x1 });
            Check.Equal("data", D(data).GetProperty("source").GetString());
            Check.Equal(Png1x1, D(data).GetProperty("data").GetString());

            await using var web = await LocalWeb.StartAsync(app =>
            {
                app.MapGet("/pic.png", () => Results.Bytes(png, "image/png"));
                app.MapGet("/page", () => Results.Content("<html></html>", "text/html"));
                app.MapGet("/gone", () => Results.StatusCode(404));
            });
            var fromUrl = await env.Run(new { url = web.Url + "/pic.png" });
            Check.False(fromUrl.IsError, fromUrl.Content);
            Check.Equal("pic.png", D(fromUrl).GetProperty("name").GetString());
            Check.Equal(web.Url + "/pic.png", D(fromUrl).GetProperty("url").GetString());
            env.Ctx.Unload();
        });

        r.Add("show_image: missing files, non-images, HTTP errors and oversized images are errors", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            Check.Contains((await env.Run(new { source = "missing.png" })).Content, "No such file");
            File.WriteAllText(Path.Combine(env.Dir, "notes.txt"), "just text");
            var text = await env.Run(new { source = "notes.txt" });
            Check.True(text.IsError);
            Check.Contains(text.Content, "is not an image");
            Check.True((await env.Run(new { })).IsError);
            Check.Contains((await env.Run(new { source = "data:image/png;base64,***" })).Content, "Not a valid data: URL");

            await using var web = await LocalWeb.StartAsync(app => app.MapGet("/gone", () => Results.StatusCode(404)));
            Check.Contains((await env.Run(new { source = web.Url + "/gone" })).Content, "HTTP 404");

            env.Ctx.SettingsFake.Set("media.maxBytes", JsonValue.Create(100_000));
            File.WriteAllBytes(Path.Combine(env.Dir, "big.png"), new byte[150_000]);
            var big = await env.Run(new { source = "big.png" });
            Check.True(big.IsError);
            Check.Contains(big.Content, "too large");
            env.Ctx.Unload();
        });

        r.Add("show_image: a length-less (chunked) body is bounded by media.maxBytes, not by HttpClient's cap", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.SettingsFake.Set("media.maxBytes", JsonValue.Create(100_000));
            var png = Convert.FromBase64String(Png1x1);
            await using var web = await LocalWeb.StartAsync(app =>
            {
                // No Content-Length: Kestrel sends chunked, so the declared-length pre-check cannot see the size.
                app.MapGet("/chunked-huge", async ctx =>
                {
                    ctx.Response.ContentType = "image/png";
                    var chunk = new byte[1024];
                    for (var i = 0; i < 300; i++) await ctx.Response.Body.WriteAsync(chunk); // ~300 KB, length unknown
                });
                app.MapGet("/chunked-small", async ctx =>
                {
                    ctx.Response.ContentType = "image/png";
                    await ctx.Response.Body.WriteAsync(png);
                });
            });
            var small = await env.Run(new { source = web.Url + "/chunked-small" });
            Check.False(small.IsError, small.Content);
            Check.Equal("image/png", D(small).GetProperty("mediaType").GetString());
            var huge = await env.Run(new { source = web.Url + "/chunked-huge" });
            Check.True(huge.IsError);
            Check.Contains(huge.Content, "larger than the");
            env.Ctx.Unload();
        });
    }
}
