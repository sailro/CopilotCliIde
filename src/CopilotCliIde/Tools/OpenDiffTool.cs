using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.VisualStudio.Extensibility;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class OpenDiffTool
{
    internal static readonly ConcurrentDictionary<string, DiffInfo> ActiveDiffs = new();

    internal record DiffInfo(string OriginalPath, string TempNewPath, string NewContent, string TabName);

    [McpServerTool(Name = "open_diff"), Description("Opens the Visual Studio diff viewer comparing original file content with proposed new content. Blocks until user accepts or rejects the diff. Returns a diff ID for accept/reject.")]
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

            // Launch VS built-in diff viewer via devenv.exe /Diff
            var devenvPath = FindDevenv();
            if (devenvPath != null)
            {
                var originalName = Path.GetFileName(original_file_path);
                var args = $"/Diff \"{original_file_path}\" \"{tempFile}\" \"{originalName} (original)\" \"{originalName} (proposed)\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = devenvPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            else
            {
                // Fallback: open both files side by side
                await extensibility.Documents().OpenTextDocumentAsync(new Uri(original_file_path), CancellationToken.None).ConfigureAwait(false);
                await extensibility.Documents().OpenTextDocumentAsync(new Uri(tempFile), CancellationToken.None).ConfigureAwait(false);
            }

            return new
            {
                success = true,
                diffId,
                original_file_path,
                proposed_file_path = tempFile,
                tab_name,
                message = $"Diff viewer opened in VS. Use 'close_diff' with diffId='{diffId}' and action='accept' to apply changes, or 'reject' to discard.",
            };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message, original_file_path, tab_name };
        }
    }

    private static string? FindDevenv()
    {
        // Search common VS installation paths
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var searchPaths = new[]
        {
            Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Community", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Professional", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Enterprise", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2025", "Community", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2025", "Professional", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2025", "Enterprise", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Community", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Professional", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Enterprise", "Common7", "IDE", "devenv.exe"),
        };

        return searchPaths.FirstOrDefault(File.Exists);
    }
}
