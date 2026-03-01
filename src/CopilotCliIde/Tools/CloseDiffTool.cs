using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class CloseDiffTool
{
    [McpServerTool(Name = "close_diff"), Description("Closes a diff by its diff ID. Use action='accept' to apply the proposed changes to the original file, or action='reject' to discard them.")]
    public static async Task<object> CloseDiffAsync(
        VisualStudioExtensibility extensibility,
        [Description("The diff ID returned by open_diff")] string diffId,
        [Description("Action to take: 'accept' applies changes to the original file, 'reject' discards them")] string action = "reject")
    {
        if (!OpenDiffTool.ActiveDiffs.TryRemove(diffId, out var diff))
        {
            return new
            {
                success = false,
                message = $"No active diff found with ID: {diffId}. It may have already been closed.",
            };
        }

        try
        {
            if (action.Equals("accept", StringComparison.OrdinalIgnoreCase))
            {
                // Apply the new content to the original file
                await File.WriteAllTextAsync(diff.OriginalPath, diff.NewContent).ConfigureAwait(false);

                // Close the temp file in VS
                try
                {
                    await extensibility.Documents()
                        .CloseDocumentAsync(new Uri(diff.TempNewPath),
                            Microsoft.VisualStudio.RpcContracts.Documents.SaveDocumentOption.NoSave,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { }
            }
            else
            {
                // Just close the temp file
                try
                {
                    await extensibility.Documents()
                        .CloseDocumentAsync(new Uri(diff.TempNewPath),
                            Microsoft.VisualStudio.RpcContracts.Documents.SaveDocumentOption.NoSave,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { }
            }

            // Clean up temp file
            try { File.Delete(diff.TempNewPath); } catch { }

            return new
            {
                success = true,
                action,
                diffId,
                tab_name = diff.TabName,
                original_file_path = diff.OriginalPath,
                message = action.Equals("accept", StringComparison.OrdinalIgnoreCase)
                    ? $"Changes applied to {diff.OriginalPath}"
                    : $"Changes discarded for {diff.OriginalPath}",
            };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message, diffId };
        }
    }
}
