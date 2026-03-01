using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;

namespace CopilotCliIde;

/// <summary>
/// Hosts an MCP server on a Windows named pipe so Copilot CLI can connect via /ide.
/// Uses ASP.NET Core Kestrel with the Streamable HTTP MCP transport.
/// </summary>
public sealed class McpPipeServer : IAsyncDisposable
{
    private WebApplication? _app;
    private string? _pipeName;
    private string? _nonce;
    private IdeDiscovery? _discovery;
    private CancellationTokenSource? _cts;

    public string? PipeName => _pipeName;

    public async Task StartAsync(VisualStudioExtensibility extensibility, IdeDiscovery discovery, CancellationToken ct)
    {
        _discovery = discovery;
        _pipeName = $"mcp-{Guid.NewGuid()}.sock";
        _nonce = Guid.NewGuid().ToString();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var builder = WebApplication.CreateBuilder();

        // Listen on named pipe only (no TCP)
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenNamedPipe(_pipeName, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
            });
        });

        // Suppress console logging from ASP.NET Core
        builder.Logging.ClearProviders();

        // Register MCP server with tools from this assembly
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();

        // Make VS extensibility available to tools via DI
        builder.Services.AddSingleton(extensibility);

        _app = builder.Build();

        // Nonce auth middleware
        var nonce = _nonce;
        _app.Use(async (context, next) =>
        {
            var auth = context.Request.Headers.Authorization.ToString();
            if (auth != $"Nonce {nonce}")
            {
                context.Response.StatusCode = 401;
                return;
            }
            await next();
        });

        _app.MapMcp();

        // Start server in background
        var appCts = _cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await _app.StartAsync(appCts.Token).ConfigureAwait(false);
                // Keep alive until cancelled
                try { await Task.Delay(Timeout.Infinite, appCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                await _app.StopAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, ct);

        // Wait briefly for server to start listening
        await Task.Delay(500, ct).ConfigureAwait(false);

        // Write lock file with workspace path
        var workspaceFolders = await GetWorkspaceFoldersAsync(extensibility).ConfigureAwait(false);
        await discovery.WriteLockFileAsync(_pipeName, _nonce, workspaceFolders).ConfigureAwait(false);
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
        if (_app != null)
            await _app.DisposeAsync().ConfigureAwait(false);
    }
}
