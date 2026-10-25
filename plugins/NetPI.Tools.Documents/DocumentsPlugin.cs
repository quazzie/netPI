using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Documents;

/// <summary>
/// <c>doc.read</c>: extract text from a document - PDF (PdfPig) or an Office file (.docx, .xlsx, .pptx,
/// the BCL's ZIP + XML) - with page/section structure, bounded per call, with a per-file extracted-text
/// cache (LRU, byte-capped, keyed by path + mtime + size) so repeated reads of a long document do not
/// re-extract it.
/// </summary>
[NetPiPlugin("netpi.tools.documents", Name = "Document tools", Description = "doc.read: extract text from PDF and Office documents (.docx, .xlsx, .pptx)", Order = 20)]
public sealed class DocumentsPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "documents", Title = "Documents", Group = "Tools", Order = 50,
            Settings =
            [
                SettingInfo.Int("documents.cacheMaxBytes", "Largest extracted-text cache (LRU, bytes)", DocCache.DefaultMaxBytes, "Older entries are evicted when the cap is exceeded.", 1024, null, "bytes"),
            ],
        });
        // One cache per plugin generation: it dies with the plugin on a hot reload, like the Files plugin's index.
        var cache = new DocCache(context.Settings);
        context.Tools.Register(new DocReadTool(context.Settings, cache));
        context.Logger.LogDebug("Document tools registered");
        return Task.CompletedTask;
    }
}
