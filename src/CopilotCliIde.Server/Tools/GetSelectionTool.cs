using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class GetSelectionTool
{
    [McpServerTool(Name = "get_selection"), Description("Get the current text selection from the active editor")]
    public static async Task<object> GetSelectionAsync(RpcClient rpcClient)
    {
        return await rpcClient.VsServices!.GetSelectionAsync();
    }
}
