namespace NetPI.Tools.Files;

/// <summary>
/// <c>files.read</c>: the bytes of a server-side file, for a client that opens it on its own machine (the remote desktop
/// shell downloads it and opens the local copy). The same path rules as the file tools: relative against the session's
/// workspace, <c>~</c> and the Git Bash spellings, <c>file://</c> accepted; a network path is refused before it is probed
/// (resolving one makes the machine authenticate against the other, the same rule <c>files.open</c> keeps).
/// </summary>
internal static class FileRead
{
    /// <summary>What a download takes by default; a bigger file is refused, not sent and failed by the client.</summary>
    public const long DefaultMaxBytes = 20L * 1024 * 1024;
    /// <summary>The hard cap: no client download past it.</summary>
    public const long HardMaxBytes = 100L * 1024 * 1024;

    public static async Task<object> ReadAsync(IPluginContext context, string root, string raw, long maxBytes, CancellationToken ct)
    {
        raw = raw.Trim().Trim('"', '\'', '`', '<', '>');
        if (raw.Length == 0) throw new RpcException("bad_request", "No path given");
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(raw, UriKind.Absolute, out var fileUri))
            raw = fileUri.LocalPath;
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
            throw new RpcException("bad_request", $"Not a local file: {raw} is a network path");
        var resolver = new ToolContext { SessionId = "", AgentId = "", CallId = "", Cwd = root, Services = context.Services, Events = context.Events };
        var full = resolver.ResolvePath(raw);
        if (Directory.Exists(full)) throw new RpcException("bad_request", $"{full} is a directory, not a file");
        if (!File.Exists(full)) throw new RpcException("not_found", $"Not found: {full}");
        var size = new FileInfo(full).Length;
        if (size > maxBytes)
            throw new RpcException("too_large", $"{full} is {size / 1024 / 1024} MiB; the download cap is {maxBytes / 1024 / 1024} MiB");
        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        return new { path = full, size, data = Convert.ToBase64String(bytes) };
    }
}
