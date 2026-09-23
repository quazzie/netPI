using Microsoft.Extensions.Logging;

namespace NetPI.Context;

/// <summary>Renders every registered <see cref="IPromptSection"/> in ascending order, skipping empty ones.</summary>
internal sealed class SystemPromptBuilder(IPluginContext ctx) : ISystemPromptBuilder
{
    public async ValueTask<string> BuildAsync(PromptContext context, CancellationToken ct)
    {
        // GetAll returns highest registration priority first; OrderBy is stable, so equal orders keep that.
        var sections = ctx.Services.GetAll<IPromptSection>().OrderBy(s => s.Order).ToList();
        var parts = new List<string>(sections.Count);
        foreach (var section in sections)
        {
            ct.ThrowIfCancellationRequested();
            string? text;
            try
            {
                text = await section.RenderAsync(context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Prompt section {Section} failed", section.Id);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
        }
        return string.Join("\n\n", parts);
    }
}
