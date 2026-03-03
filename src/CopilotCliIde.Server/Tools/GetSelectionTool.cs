using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class GetSelectionTool
{
	[McpServerTool(Name = "get_selection"), Description("Get text selection. Returns current selection if an editor is active, otherwise returns the latest cached selection. The \"current\" field indicates if this is from the active editor (true) or cached (false).")]
	public static async Task<object> GetSelectionAsync(RpcClient rpcClient)
	{
		return await rpcClient.VsServices!.GetSelectionAsync();
	}
}
