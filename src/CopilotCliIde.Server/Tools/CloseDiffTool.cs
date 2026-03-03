using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class CloseDiffTool
{
	[McpServerTool(Name = "close_diff"), Description("Closes a diff. action='accept' applies changes, 'reject' discards.")]
	public static async Task<object> CloseDiffAsync(
		RpcClient rpcClient,
		[Description("The diff ID returned by open_diff")] string diffId,
		[Description("'accept' or 'reject'")] string action = "reject")
	{
		return await rpcClient.VsServices!.CloseDiffAsync(diffId, action);
	}
}
