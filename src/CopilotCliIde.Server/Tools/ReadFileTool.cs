using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class ReadFileTool
{
	[McpServerTool(Name = "read_file", TaskSupport = ToolTaskSupport.Forbidden), Description("Read the content of a file.")]
	public static async Task<object> ReadFileAsync(
		RpcClient rpcClient,
		[Description("Absolute path to the file to read")] string filePath,
		[Description("Optional 1-based start line")] int? startLine = null,
		[Description("Optional max lines to read")] int? maxLines = null)
	{
		return await rpcClient.VsServices!.ReadFileAsync(filePath, startLine, maxLines);
	}
}
