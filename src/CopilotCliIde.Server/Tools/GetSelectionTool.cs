using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class GetSelectionTool
{
	[McpServerTool(Name = "get_selection", TaskSupport = ToolTaskSupport.Forbidden), Description("Get text selection. Returns current selection if an editor is active, otherwise returns the latest cached selection. The \"current\" field indicates if this is from the active editor (true) or cached (false).")]
	public static async Task<object> GetSelectionAsync(RpcClient rpcClient)
	{
		var result = await rpcClient.VsServices!.GetSelectionAsync();
		// Return anonymous object so text is always present on the wire (MCP SDK omits null properties)
		return new
		{
			text = result.Text ?? "",
			filePath = result.FilePath,
			fileUrl = result.FileUrl,
			selection = result.Selection,
			current = result.Current
		};
	}
}
