using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.RpcContracts.OpenDocument;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class OpenDiffTool
{
    internal static readonly ConcurrentDictionary<string, DiffInfo> ActiveDiffs = new();

    internal record DiffInfo(string OriginalPath, string TempNewPath, string NewContent, string TabName);

    [McpServerTool(Name = "open_diff"), Description("Opens the proposed file changes in Visual Studio for review. Creates a temporary file with the new content alongside the original for comparison. Returns a diff ID for accept/reject.")]
    public static async Task<object> OpenDiffAsync(
        VisualStudioExtensibility extensibility,
        [Description("Path to the original file")] string original_file_path,
        [Description("The new file contents to compare against")] string new_file_contents,
        [Description("Name for the diff tab")] string tab_name)
    {
        try
        {
            var ext = Path.GetExtension(original_file_path);
            var tempDir = Path.Combine(Path.GetTempPath(), "copilot-cli-diffs");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, $"{tab_name}-proposed{ext}");
            await File.WriteAllTextAsync(tempFile, new_file_contents).ConfigureAwait(false);

            var diffId = $"{DateTime.UtcNow.Ticks}-{tab_name}";
            ActiveDiffs[diffId] = new DiffInfo(original_file_path, tempFile, new_file_contents, tab_name);

            // Open the proposed file in VS — the user can compare with the original
            // which is already open or can be opened alongside
            await extensibility.Documents().OpenTextDocumentAsync(
                new Uri(tempFile), CancellationToken.None).ConfigureAwait(false);

            return new
            {
                success = true,
                diffId,
                original_file_path,
                proposed_file_path = tempFile,
                tab_name,
                message = $"Proposed changes opened in VS as '{Path.GetFileName(tempFile)}'. Compare with the original file. Use 'close_diff' with diffId='{diffId}' and action='accept' to apply, or 'reject' to discard.",
            };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message, original_file_path, tab_name };
        }
    }
}
