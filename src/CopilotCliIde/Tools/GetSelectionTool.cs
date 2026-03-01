using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class GetSelectionTool
{
    [McpServerTool(Name = "get_selection"), Description("Get the list of currently open documents in Visual Studio with their file paths and dirty status")]
    public static async Task<object> GetSelectionAsync(VisualStudioExtensibility extensibility)
    {
        try
        {
            var docs = await extensibility.Documents().GetOpenDocumentsAsync(CancellationToken.None).ConfigureAwait(false);

            var results = new List<object>();
            foreach (var doc in docs)
            {
                var entry = new Dictionary<string, object?>
                {
                    ["filePath"] = doc.Moniker.LocalPath,
                    ["isDirty"] = doc.IsDirty,
                    ["isReadOnly"] = doc.IsReadOnly,
                };

                // Try to get text content for initialized documents
                try
                {
                    var textDoc = await extensibility.Documents()
                        .GetTextDocumentSnapshotAsync(doc, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (textDoc != null)
                    {
                        entry["lineCount"] = textDoc.Lines.Count;
                        entry["uri"] = textDoc.Uri?.ToString();
                    }
                }
                catch { }

                results.Add(entry);
            }

            return new { documents = results, count = results.Count };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }
}
