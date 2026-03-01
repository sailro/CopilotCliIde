using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class UpdateSessionNameTool
{
    [McpServerTool(Name = "update_session_name"), Description("Update the display name for the current CLI session")]
    public static object UpdateSessionName(
        [Description("The new session name")] string name)
    {
        return new { success = true };
    }
}
