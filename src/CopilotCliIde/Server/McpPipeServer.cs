using System.IO.Pipes;
using System.Reflection;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CopilotCliIde;

/// <summary>
/// Hosts an MCP server on a Windows named pipe so Copilot CLI can connect via /ide.
/// Uses NamedPipeServerStream + MCP StreamServerTransport (no ASP.NET Core dependency).
/// </summary>
public sealed class McpPipeServer : IAsyncDisposable
{
    private string? _pipeName;
    private string? _nonce;
    private IdeDiscovery? _discovery;
    private CancellationTokenSource? _cts;
    private McpServerOptions? _serverOptions;
    private VisualStudioExtensibility? _extensibility;

    public string? PipeName => _pipeName;

    public async Task StartAsync(VisualStudioExtensibility extensibility, IdeDiscovery discovery, CancellationToken ct)
    {
        _discovery = discovery;
        _extensibility = extensibility;
        _pipeName = $"mcp-{Guid.NewGuid()}.sock";
        _nonce = Guid.NewGuid().ToString();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "Visual Studio", Version = "1.0.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            ToolCollection = [],
        };

        // Scan assembly for [McpServerToolType] classes and their [McpServerTool] methods
        var assembly = typeof(McpPipeServer).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null)
                continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() == null)
                    continue;

                var tool = McpServerTool.Create(method);
                _serverOptions.ToolCollection.Add(tool);
            }
        }

        // Start accepting connections in background
        _ = Task.Run(() => AcceptConnectionsAsync(_cts.Token), ct);

        // Wait briefly for pipe to be created
        await Task.Delay(200, ct).ConfigureAwait(false);

        // Write lock file with workspace path
        var workspaceFolders = await GetWorkspaceFoldersAsync(extensibility).ConfigureAwait(false);
        await discovery.WriteLockFileAsync(_pipeName, _nonce, workspaceFolders).ConfigureAwait(false);
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName!,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // Handle each connection independently
                _ = Task.Run(() => HandleConnectionAsync(pipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var serviceProvider = new SingletonServiceProvider(_extensibility!);
            await using var transport = new StreamServerTransport(pipe, pipe);
            await using var server = McpServer.Create(transport, _serverOptions!, serviceProvider: serviceProvider);
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static async Task<IReadOnlyList<string>> GetWorkspaceFoldersAsync(VisualStudioExtensibility extensibility)
    {
        try
        {
            var result = await extensibility.Workspaces().QuerySolutionAsync(
                query => query.With(q => q.Directory),
                CancellationToken.None).ConfigureAwait(false);

            var solutionDir = result.FirstOrDefault()?.Directory;
            if (!string.IsNullOrEmpty(solutionDir))
                return [solutionDir];
        }
        catch
        {
            // API might not be available or no solution loaded
        }

        return [Directory.GetCurrentDirectory()];
    }

    public async ValueTask DisposeAsync()
    {
        _discovery?.RemoveLockFile();
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Minimal IServiceProvider that resolves VisualStudioExtensibility for MCP tool DI.
    /// </summary>
    private sealed class SingletonServiceProvider(VisualStudioExtensibility extensibility) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(VisualStudioExtensibility) ? extensibility : null;
    }
}
