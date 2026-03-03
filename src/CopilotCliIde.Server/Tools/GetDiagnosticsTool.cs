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
		return await rpcClient.VsServices!.GetDiagnosticsAsync(uri);
	}
}
