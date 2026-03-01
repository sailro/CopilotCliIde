using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class GetSelectionTool
{
    [McpServerTool(Name = "get_selection"), Description("Get the current text selection from the active editor in Visual Studio, including selected text, file path, and line/column range")]
    public static async Task<object> GetSelectionAsync(VisualStudioExtensibility extensibility)
    {
        var selection = SelectionTracker.LastSelection;

        // Also get open documents list
        List<object>? openDocs = null;
        try
        {
            var docs = await extensibility.Documents().GetOpenDocumentsAsync(CancellationToken.None).ConfigureAwait(false);
            openDocs = docs.Select(d => new
            {
                filePath = d.Moniker.LocalPath,
                isDirty = d.IsDirty,
            } as object).ToList();
        }
        catch { }

        if (selection == null)
        {
            return new
            {
                current = false,
                message = "No selection tracked yet. Open or click in a file in Visual Studio.",
                openDocuments = openDocs,
            };
        }

        return new
        {
            current = true,
            filePath = selection.FilePath,
            fileUrl = selection.FileUri,
            text = selection.SelectedText,
            selection = new
            {
                start = new { line = selection.StartLine, character = selection.StartColumn },
                end = new { line = selection.EndLine, character = selection.EndColumn },
                isEmpty = selection.IsEmpty,
            },
            timestamp = selection.Timestamp.ToString("O"),
            openDocuments = openDocs,
        };
    }
}
