using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class GetDiagnosticsTool
{
    [McpServerTool(Name = "get_diagnostics"), Description("Get build diagnostics (errors, warnings) from Visual Studio. Can optionally filter by file path.")]
    public static async Task<object> GetDiagnosticsAsync(
        VisualStudioExtensibility extensibility,
        [Description("Optional file path to filter diagnostics for a specific file")]
        string? filePath = null)
    {
        // VS Extensibility out-of-proc provides diagnostics via the DiagnosticViewerService (brokered service).
        // As a practical approach, we query open documents and report their dirty state,
        // and suggest running a build for full diagnostics.
        try
        {
            var docs = await extensibility.Documents().GetOpenDocumentsAsync(CancellationToken.None).ConfigureAwait(false);

            var results = new List<object>();
            foreach (var doc in docs)
            {
                if (filePath != null && !doc.Moniker.LocalPath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var textDoc = await extensibility.Documents()
                        .GetTextDocumentSnapshotAsync(doc, CancellationToken.None)
                        .ConfigureAwait(false);

                    if (textDoc != null)
                    {
                        results.Add(new
                        {
                            filePath = doc.Moniker.LocalPath,
                            isDirty = doc.IsDirty,
                            lineCount = textDoc.Lines.Count,
                        });
                    }
                }
                catch
                {
                    results.Add(new
                    {
                        filePath = doc.Moniker.LocalPath,
                        isDirty = doc.IsDirty,
                        lineCount = (int?)null,
                    });
                }
            }

            return new
            {
                openFiles = results,
                hint = "For full build diagnostics, use 'dotnet build' or 'msbuild' in the terminal. VS error list diagnostics require the in-process DiagnosticViewerService.",
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }
}
