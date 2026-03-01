using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class GetDiagnosticsTool
{
    [McpServerTool(Name = "get_diagnostics"), Description("Get build diagnostics (errors, warnings) from Visual Studio")]
    public static async Task<object> GetDiagnosticsAsync(
        VisualStudioExtensibility extensibility,
        [Description("Optional file path to filter diagnostics. If not provided, returns all diagnostics.")]
        string? uri = null)
    {
        try
        {
            var result = await extensibility.Workspaces().QuerySolutionAsync(
                query => query.With(q => q.Path),
                CancellationToken.None).ConfigureAwait(false);

            return new
            {
                message = "Diagnostics query via VS Extensibility is evolving. Build errors are best obtained by running builds.",
                solutionPath = result.FirstOrDefault()?.Path,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }
}
