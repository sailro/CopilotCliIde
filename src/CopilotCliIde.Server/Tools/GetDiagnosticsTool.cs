using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class GetDiagnosticsTool
{
	[McpServerTool(Name = "get_diagnostics"), Description("Gets language diagnostics (errors, warnings, hints) from VS Code")]
	public static async Task<object> GetDiagnosticsAsync(
		RpcClient rpcClient,
		[Description("File URI to get diagnostics for. Optional. If not provided, returns diagnostics for all files.")] string? uri = null)
	{
		var result = await rpcClient.VsServices!.GetDiagnosticsAsync(uri);
		if (result.Error != null)
			return new { error = result.Error };
		// Return the file list directly — VS Code returns a JSON array at root
		return result.Files ?? [];
	}
}
