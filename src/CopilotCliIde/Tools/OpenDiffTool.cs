using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class OpenDiffTool
{
    internal static readonly ConcurrentDictionary<string, DiffInfo> ActiveDiffs = new();

    internal record DiffInfo(string OriginalPath, string TempNewPath, string NewContent, string TabName);

    [McpServerTool(Name = "open_diff"), Description("Opens a diff view comparing original file content with new content. Writes the proposed content to a temp file and opens both in Visual Studio. Returns a diff ID for accept/reject.")]
    public static async Task<object> OpenDiffAsync(
        VisualStudioExtensibility extensibility,
        [Description("Path to the original file")] string original_file_path,
        [Description("The new file contents to compare against")] string new_file_contents,
        [Description("Name for the diff tab")] string tab_name)
    {
        try
        {
            // Create temp file for the proposed new content
            var ext = Path.GetExtension(original_file_path);
            var tempDir = Path.Combine(Path.GetTempPath(), "copilot-cli-diffs");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, $"{tab_name}-proposed{ext}");
            await File.WriteAllTextAsync(tempFile, new_file_contents).ConfigureAwait(false);

            // Track the diff
            var diffId = $"{DateTime.UtcNow.Ticks}-{tab_name}";
            ActiveDiffs[diffId] = new DiffInfo(original_file_path, tempFile, new_file_contents, tab_name);

            // Open both files in VS so the user can compare
            var originalUri = new Uri(original_file_path);
            var proposedUri = new Uri(tempFile);

            await extensibility.Documents().OpenTextDocumentAsync(originalUri, CancellationToken.None).ConfigureAwait(false);
            await extensibility.Documents().OpenTextDocumentAsync(proposedUri, CancellationToken.None).ConfigureAwait(false);

            return new
            {
                success = true,
                diffId,
                original_file_path,
                proposed_file_path = tempFile,
                tab_name,
                message = $"Opened original and proposed files in VS. Use 'close_diff' with diffId='{diffId}' to accept (apply changes) or reject (discard).",
            };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message, original_file_path, tab_name };
        }
    }
}
