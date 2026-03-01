using System.ComponentModel;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class GetVsInfoTool
{
    [McpServerTool(Name = "get_vs_info"), Description("Get information about the current Visual Studio instance, including open solution, projects, and open documents")]
    public static async Task<object> GetVsInfoAsync(VisualStudioExtensibility extensibility)
    {
        string? solutionPath = null;
        string? solutionName = null;
        string? solutionDir = null;
        List<object>? projects = null;
        List<string>? openDocs = null;

        try
        {
            var result = await extensibility.Workspaces().QuerySolutionAsync(
                query => query
                    .With(q => q.Path)
                    .With(q => q.BaseName)
                    .With(q => q.Directory)
                    .With(q => q.ActiveConfiguration)
                    .With(q => q.ActivePlatform),
                CancellationToken.None).ConfigureAwait(false);

            var sol = result.FirstOrDefault();
            solutionPath = sol?.Path;
            solutionName = sol?.BaseName;
            solutionDir = sol?.Directory;
        }
        catch { }

        try
        {
            var projectResults = await extensibility.Workspaces().QueryProjectsAsync(
                query => query
                    .With(p => p.Name)
                    .With(p => p.Path)
                    .With(p => p.ActiveConfigurations),
                CancellationToken.None).ConfigureAwait(false);

            projects = [];
            foreach (var p in projectResults)
                projects.Add(new { p.Name, p.Path });
        }
        catch { }

        try
        {
            var docs = await extensibility.Documents().GetOpenDocumentsAsync(CancellationToken.None).ConfigureAwait(false);
            openDocs = docs.Select(d => d.Moniker.LocalPath).ToList();
        }
        catch { }

        return new
        {
            ideName = "Visual Studio",
            solutionPath,
            solutionName,
            solutionDirectory = solutionDir,
            projects,
            openDocuments = openDocs,
            processId = Environment.ProcessId,
        };
    }
}
