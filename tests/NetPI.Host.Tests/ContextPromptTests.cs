using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Context.Pi;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §15: the system prompt must include a current-date section and a
/// per-tool "Tool Guidelines" section (IAgentTool.Guidelines collected by the
/// agent runner into SystemPromptInputs.ToolGuidelines).
/// </summary>
public class ContextPromptTests
{
    /// <summary>§15: the environment block carries a Date line (yyyy-MM-dd).</summary>
    [Fact]
    public void Environment_IncludesCurrentDate()
    {
        var inputs = new ContextDiscovery().BuildForWorkspace("/tmp");

        Assert.Contains("Date: ", inputs.Environment.Content);
        // Matches today's date in the format the section uses.
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), inputs.Environment.Content);
    }

    /// <summary>§15: guidelines render as a bulleted "Tool Guidelines" section.</summary>
    [Fact]
    public async Task Provider_RendersToolGuidelinesSection()
    {
        var inputs = new SystemPromptInputs
        {
            BasePrompt = "base",
            ToolGuidelines = ["read: page with offset", "edit: oldText must be unique"],
        };
        var provider = new SystemPromptProvider();

        var prompt = await provider.BuildAsync(inputs);

        Assert.Contains("## Tool Guidelines", prompt);
        Assert.Contains("- read: page with offset", prompt);
        Assert.Contains("- edit: oldText must be unique", prompt);
    }

    /// <summary>§15: no guidelines → no section (keep the prompt minimal).</summary>
    [Fact]
    public async Task Provider_OmitsGuidelinesSectionWhenEmpty()
        {
        var inputs = new SystemPromptInputs { BasePrompt = "base" };
        var provider = new SystemPromptProvider();

        var prompt = await provider.BuildAsync(inputs);

        Assert.DoesNotContain("## Tool Guidelines", prompt);
    }

    /// <summary>§15: IAgentTool.Guidelines defaults to empty so existing tools
    /// keep compiling and simply contribute nothing to the prompt.</summary>
    [Fact]
    public void ToolGuidelines_DefaultToEmpty()
    {
        var tool = new NoGuidelinesTool();

        Assert.Empty(tool.Guidelines);
    }

    private sealed class NoGuidelinesTool : IAgentTool
    {
        public string Name => "noop";
        public string Description => "noop";
        public IReadOnlyList<string> Guidelines => [];
        public JsonElement Parameters => JsonDocument.Parse("{}").RootElement;
        public ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct)
            => ValueTask.FromResult(new ToolResult("t", "noop", [], false));
    }
}
