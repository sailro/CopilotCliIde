using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class GetVsInfoTool
{
	[McpServerTool(Name = "get_vscode_info"), Description("Get information about the current VS Code instance")]
	public static async Task<object> GetVsInfoAsync(RpcClient rpcClient)
	{
		return await rpcClient.VsServices!.GetVsInfoAsync();
	}
}
