using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class GetVsInfoTool
{
	[McpServerTool(Name = "get_vscode_info", TaskSupport = ToolTaskSupport.Forbidden), Description("Get information about the current VS Code instance")]
	public static async Task<object> GetVsInfoAsync(RpcClient rpcClient)
	{
		return await rpcClient.VsServices!.GetVsInfoAsync();
	}
}
