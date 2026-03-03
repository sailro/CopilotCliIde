using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class OpenDiffTool
{
	[McpServerTool(Name = "open_diff"), Description("Opens a diff view comparing original file content with new content. Blocks until user accepts, rejects, or closes the diff.")]
	public static async Task<object> OpenDiffAsync(
		RpcClient rpcClient,
		[Description("Path to the original file")] string original_file_path,
		[Description("The new file contents to compare against")] string new_file_contents,
		[Description("Name for the diff tab")] string tab_name)
	{
		var result = await rpcClient.VsServices!.OpenDiffAsync(original_file_path, new_file_contents, tab_name);
		return result;
	}
}
