using System.Text.Json;

namespace NetPI.Tools.Files;

/// <summary>Common plumbing: lenient args, uniform error handling (errors are returned, never thrown).</summary>
public abstract class FileToolBase(ISettings? settings) : IAgentTool
{
    /// <summary>Files larger than this are not loaded for editing/diffing.</summary>
    public const long MaxEditableBytes = 64L * 1024 * 1024;

    // Guidelines the file tools share: every tool a line concerns carries it, and the prompt lists it once.
    internal const string UseFileTools =
        "Use the file tools for files, not cat, sed, grep, find or echo > in the shell. They handle line endings, encodings and BOMs: never convert them yourself.";
    internal const string ChangeFiles =
        "Change existing files with edit, copying oldText verbatim from read output, and make all the changes to a file in one call; use write only for new files and complete rewrites of small ones.";

    protected ISettings? Settings { get; } = settings;

    public abstract ToolDefinition Definition { get; }

    // The workspace rule for a mutation (write, edit) is the shared one: WorkspacePaths.MutationRefusalAsync, the same
    // answer the ssh download gives, and the one the workspace guard hook gives before the call reaches a tool.

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

    protected static ToolResult NotFound(ToolContext ctx, string full, string? extra = null) => ToolResult.Error(NotFoundText(full, extra));

    protected static string NotFoundText(string full, string? extra = null)
    {
        var msg = $"File not found: {full}";
        if (PathDisplay.Suggest(full) is { } s) msg += ". " + s;
        else msg += ".";
        if (extra is not null) msg += " " + extra;
        return msg;
    }

    /// <summary>EOL for new files from settings (files.newFileEol: lf | crlf | auto).</summary>
    protected EolStyle NewFileEol() =>
        TextCodec.ParseEol(Settings.GetOr("files.newFileEol", "lf")) ?? EolStyle.Lf;
}
