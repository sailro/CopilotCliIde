using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace CopilotCliIde;

/// <summary>
/// Extension entry point. Starts the MCP server on load so Copilot CLI can discover it via /ide.
/// </summary>
[VisualStudioContribution]
public class CopilotCliIdeExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "CopilotCliIde.a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d",
            version: this.ExtensionAssemblyVersion,
            publisherName: "CopilotCliIde",
            displayName: "Copilot CLI IDE Bridge",
            description: "Enables GitHub Copilot CLI to interact with Visual Studio via the /ide command"),
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
        serviceCollection.AddSingleton<McpPipeServer>();
        serviceCollection.AddSingleton<IdeDiscovery>();
    }

    protected override async Task OnInitializedAsync(VisualStudioExtensibility extensibility, CancellationToken cancellationToken)
    {
        await base.OnInitializedAsync(extensibility, cancellationToken);

        var server = this.ServiceProvider.GetRequiredService<McpPipeServer>();
        var discovery = this.ServiceProvider.GetRequiredService<IdeDiscovery>();

        // Clean stale lock files from previous VS sessions
        await discovery.CleanStaleLockFilesAsync();

        // Start MCP server and write lock file
        await server.StartAsync(extensibility, discovery, cancellationToken);
    }
}
