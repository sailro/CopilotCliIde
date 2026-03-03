using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class CloseDiffTool
{
	[McpServerTool(Name = "close_diff"), Description("Closes a diff tab by its tab name. Use this when the client rejects an edit to close the corresponding diff view.")]
	public static async Task<object> CloseDiffAsync(
		RpcClient rpcClient,
		[Description("The tab name of the diff to close (must match the tab_name used when opening the diff)")] string tab_name)
	{
		return await rpcClient.VsServices!.CloseDiffByTabNameAsync(tab_name);
	}
}
