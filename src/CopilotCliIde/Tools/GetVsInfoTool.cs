using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class GetVsInfoTool
{
    [McpServerTool(Name = "get_vs_info"), Description("Get information about the current Visual Studio instance")]
    public static async Task<object> GetVsInfoAsync(VisualStudioExtensibility extensibility)
    {
        string? solutionPath = null;
        string? solutionName = null;

        try
        {
            var result = await extensibility.Workspaces().QuerySolutionAsync(
                query => query.With(q => q.Path).With(q => q.BaseName),
                CancellationToken.None).ConfigureAwait(false);
            var sol = result.FirstOrDefault();
            solutionPath = sol?.Path;
            solutionName = sol?.BaseName;
        }
        catch { }

        return new
        {
            ideName = "Visual Studio",
            solutionPath,
            solutionName,
            processId = Environment.ProcessId,
        };
    }
}
