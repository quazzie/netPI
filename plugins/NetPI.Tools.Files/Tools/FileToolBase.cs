using System.Text.Json;

namespace NetPI.Tools.Files;

/// <summary>Common plumbing: lenient args, uniform error handling (errors are returned, never thrown).</summary>
public abstract class FileToolBase(ISettings? settings) : IAgentTool
{
    internal static readonly string[] PathNames = ["path", "file_path", "filePath", "file", "filename", "fileName", "target"];

    /// <summary>Files larger than this are not loaded for editing/diffing.</summary>
    public const long MaxEditableBytes = 64L * 1024 * 1024;

    protected ISettings? Settings { get; } = settings;

    public abstract ToolDefinition Definition { get; }

    protected abstract Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        try
        {
            return await RunAsync(context, new ToolArgs(args), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Error($"Permission denied: {ex.Message}");
        }
        catch (PathTooLongException ex)
        {
            return ToolResult.Error($"Path too long: {ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolResult.Error($"I/O error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{Definition.Name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected static string Rel(ToolContext ctx, string full) => PathDisplay.Relative(ctx.Cwd, full);

    protected static ToolResult MissingArg(string name, string example) =>
        ToolResult.Error($"Missing required argument '{name}'. Example: {example}");

    protected static ToolResult NotFound(ToolContext ctx, string full, string? extra = null)
    {
        var msg = $"File not found: {full}";
        if (PathDisplay.Suggest(full) is { } s) msg += ". " + s;
        else msg += ".";
        if (extra is not null) msg += " " + extra;
        return ToolResult.Error(msg);
    }

    /// <summary>EOL for new files from settings (files.newFileEol: lf | crlf | auto).</summary>
    protected EolStyle NewFileEol() =>
        TextCodec.ParseEol(Settings.SafeGet("files.newFileEol", "lf")) ?? EolStyle.Lf;
}
