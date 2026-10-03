using System.Text.Json;

namespace NetPI.Tools.Files;

/// <summary>Common plumbing: lenient args, uniform error handling (errors are returned, never thrown).</summary>
public abstract class FileToolBase(ISettings? settings) : IAgentTool
{
    internal static readonly string[] PathNames = ["path", "file_path", "filePath", "file", "filename", "fileName", "target"];

    /// <summary>Files larger than this are not loaded for editing/diffing.</summary>
    public const long MaxEditableBytes = 64L * 1024 * 1024;

    // Guidelines the file tools share: every tool a line concerns carries it, and the prompt lists it once.
    internal const string UseFileTools =
        "Use the file tools for files, not cat, sed, grep, find or echo > in the shell. They handle line endings, encodings and BOMs: never convert them yourself.";
    internal const string ChangeFiles =
        "Change existing files with edit, copying oldText verbatim from read output, and make all the changes to a file in one call; use write only for new files and complete rewrites of small ones.";

    protected ISettings? Settings { get; } = settings;

    public abstract ToolDefinition Definition { get; }

    /// <summary>
    /// The workspace rule for a mutation, in the file tools' own base class so <c>write</c>, <c>edit</c> and anything
    /// added later obey the same one: an unbound session may write anywhere (the behavior before workspaces existed),
    /// and an isolated one may not write into another checkout of the same repository. Returns null when the write is
    /// allowed, and the refusal otherwise — the hook in the workspace plugin already blocks most of these, this is the
    /// tool's own answer so a call made without hooks (a test, another caller) is not unprotected either.
    /// </summary>
    protected static string? WorkspaceRefusal(ToolContext ctx, string fullPath)
    {
        var binding = ctx.Workspace();
        if (binding is null || !binding.Isolated) return null;
        var probe = ctx.Services?.Get<IWorkspaceRepoProbe>();
        var verdict = WorkspacePaths.CheckMutation(binding, fullPath, probe);
        return verdict is WorkspacePathVerdict.ForeignCheckout or WorkspacePathVerdict.Unverifiable
            ? WorkspacePaths.Refusal(binding, fullPath, probe, verdict)
            : null;
    }

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
        TextCodec.ParseEol(Settings.GetOr("files.newFileEol", "lf")) ?? EolStyle.Lf;
}
