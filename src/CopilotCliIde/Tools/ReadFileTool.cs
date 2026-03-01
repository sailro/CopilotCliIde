using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class ReadFileTool
{
    [McpServerTool(Name = "read_file"), Description("Read the content of a file open in Visual Studio, including unsaved changes. Returns the editor buffer content.")]
    public static async Task<object> ReadFileAsync(
        VisualStudioExtensibility extensibility,
        [Description("Absolute path to the file to read")] string filePath,
        [Description("Optional 1-based line number to start reading from")] int? startLine = null,
        [Description("Optional maximum number of lines to read")] int? maxLines = null)
    {
        try
        {
            var uri = new Uri(filePath);
            var textDoc = await extensibility.Documents()
                .OpenTextDocumentAsync(uri, CancellationToken.None)
                .ConfigureAwait(false);

            // Get full text via TextRange.CopyTo
            var text = textDoc.Text;
            var chars = new char[text.Length];
            text.CopyTo(chars);
            var fullText = new string(chars);

            var allLines = fullText.Split('\n');
            var totalLines = allLines.Length;

            var start = Math.Max(0, (startLine ?? 1) - 1);
            var count = maxLines ?? totalLines;
            var end = Math.Min(totalLines, start + count);

            var content = string.Join("\n", allLines[start..end]);

            return new
            {
                filePath = textDoc.Uri?.LocalPath ?? filePath,
                content,
                totalLines,
                startLine = start + 1,
                linesReturned = end - start,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, filePath };
        }
    }
}
