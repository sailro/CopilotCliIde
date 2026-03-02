using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class UpdateSessionNameTool
{
    [McpServerTool(Name = "update_session_name"), Description("Update the display name for the current CLI session")]
#pragma warning disable IDE0060 // Parameter is part of MCP tool schema
    public static object UpdateSessionName([Description("The new session name")] string name)
#pragma warning restore IDE0060
    {
        return new { success = true };
    }
}
