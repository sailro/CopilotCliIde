using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class OpenDiffTool
{
    [McpServerTool(Name = "open_diff"), Description("Opens a diff view in Visual Studio comparing original file content with new content. Returns immediately with a diff ID.")]
    public static object OpenDiff(
        [Description("Path to the original file")] string original_file_path,
        [Description("The new file contents to compare against")] string new_file_contents,
        [Description("Name for the diff tab")] string tab_name)
    {
        // VS Extensibility out-of-proc does not currently expose diff viewer APIs.
        // In the future, this could use IVsDifferenceService via in-proc interop.
        return new
        {
            success = false,
            message = "Diff viewer not yet available in VS Extensibility out-of-proc model. " +
                      "Use 'git diff' or write files and compare manually.",
            original_file_path,
            tab_name
        };
    }
}
