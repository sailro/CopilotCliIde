using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class CloseDiffTool
{
    [McpServerTool(Name = "close_diff"), Description("Closes a diff tab by its tab name")]
    public static object CloseDiff(
        [Description("The tab name of the diff to close")] string tab_name)
    {
        return new
        {
            success = true,
            already_closed = true,
            tab_name,
            message = $"No active diff found with tab name: {tab_name}"
        };
    }
}
