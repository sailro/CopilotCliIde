using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tools;

[McpServerToolType]
public sealed class GetDiagnosticsTool
{
    [McpServerTool(Name = "get_diagnostics"), Description("Get build diagnostics (errors, warnings) from Visual Studio.")]
    public static async Task<object> GetDiagnosticsAsync(
        RpcClient rpcClient,
        [Description("Optional file path to filter diagnostics")] string? filePath = null)
    {
        return await rpcClient.VsServices!.GetDiagnosticsAsync(filePath);
    }
}
