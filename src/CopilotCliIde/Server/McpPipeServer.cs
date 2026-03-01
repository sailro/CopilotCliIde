using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CopilotCliIde;

/// <summary>
/// Hosts an MCP server on a Windows named pipe so Copilot CLI can connect via /ide.
/// Copilot CLI connects via HTTP (Streamable HTTP MCP transport) over the named pipe.
/// We use a raw HTTP listener on the named pipe to handle requests.
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

    /// <summary>
    /// Handles a single pipe connection. The CLI sends HTTP requests (Streamable HTTP MCP transport)
    /// over the pipe. We parse them minimally and route to the MCP server via StreamServerTransport.
    /// </summary>
    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            // The MCP Streamable HTTP transport sends HTTP POST requests to /mcp
            // We need to handle this as a simple HTTP server on the raw pipe stream.
            // Each HTTP request/response pair uses the pipe.
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                // Read HTTP request
                var (method, path, headers, body) = await ReadHttpRequestAsync(pipe, ct).ConfigureAwait(false);
                if (method == null) break; // Connection closed

                // Validate nonce
                headers.TryGetValue("authorization", out var auth);
                if (auth != $"Nonce {_nonce}")
                {
                    await WriteHttpResponseAsync(pipe, 401, "Unauthorized", ct).ConfigureAwait(false);
                    continue;
                }

                if (method == "GET" && path == "/mcp")
                {
                    // SSE endpoint for server-to-client notifications - send 405 for now
                    await WriteHttpResponseAsync(pipe, 405, "Method Not Allowed", ct).ConfigureAwait(false);
                    continue;
                }

                if (method == "DELETE" && path == "/mcp")
                {
                    await WriteHttpResponseAsync(pipe, 200, "{}", ct).ConfigureAwait(false);
                    continue;
                }

                if (method == "POST" && path == "/mcp")
                {
                    // Parse JSON-RPC request and handle via MCP server
                    var response = await HandleMcpRequestAsync(body, ct).ConfigureAwait(false);
                    await WriteHttpResponseAsync(pipe, 200, response, ct,
                        contentType: "application/json",
                        extraHeaders: "Mcp-Session-Id: vs-session\r\n").ConfigureAwait(false);
                    continue;
                }

                await WriteHttpResponseAsync(pipe, 404, "Not Found", ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private McpServer? _mcpServer;
    private StreamServerTransport? _mcpTransport;

    private async Task<string> HandleMcpRequestAsync(string body, CancellationToken ct)
    {
        // Use a pair of memory streams to feed the JSON-RPC message to the MCP server
        // and capture the response
        var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(body + "\n"));
        var outputStream = new MemoryStream();

        var serviceProvider = new SingletonServiceProvider(_extensibility!);
        await using var transport = new StreamServerTransport(inputStream, outputStream);
        await using var server = McpServer.Create(transport, _serverOptions!, serviceProvider: serviceProvider);

        // Start the server (it will process the single message and the input stream will end)
        var runTask = server.RunAsync(ct);

        // Give it time to process
        await Task.WhenAny(runTask, Task.Delay(5000, ct)).ConfigureAwait(false);

        outputStream.Position = 0;
        var response = Encoding.UTF8.GetString(outputStream.ToArray()).Trim();

        return string.IsNullOrEmpty(response) ? "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"No response\"},\"id\":null}" : response;
    }

    private static async Task<(string? method, string? path, Dictionary<string, string> headers, string body)> ReadHttpRequestAsync(Stream stream, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        var buffer = new byte[1];
        var headerComplete = false;

        // Read headers byte by byte until \r\n\r\n
        while (!headerComplete && !ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return (null, null, headers, "");

            sb.Append((char)buffer[0]);
            if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
                headerComplete = true;
        }

        var headerText = sb.ToString();
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return (null, null, headers, "");

        // Parse request line
        var parts = lines[0].Split(' ');
        var method = parts.Length > 0 ? parts[0] : null;
        var path = parts.Length > 1 ? parts[1] : null;

        // Parse headers
        for (int i = 1; i < lines.Length; i++)
        {
            var colonIdx = lines[i].IndexOf(':');
            if (colonIdx > 0)
            {
                var key = lines[i][..colonIdx].Trim();
                var value = lines[i][(colonIdx + 1)..].Trim();
                headers[key] = value;
            }
        }

        // Read body if Content-Length present
        var body = "";
        if (headers.TryGetValue("content-length", out var clStr) && int.TryParse(clStr, out var contentLength) && contentLength > 0)
        {
            var bodyBuffer = new byte[contentLength];
            var totalRead = 0;
            while (totalRead < contentLength)
            {
                var read = await stream.ReadAsync(bodyBuffer.AsMemory(totalRead, contentLength - totalRead), ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }
            body = Encoding.UTF8.GetString(bodyBuffer, 0, totalRead);
        }

        return (method, path, headers, body);
    }

    private static async Task WriteHttpResponseAsync(Stream stream, int statusCode, string body, CancellationToken ct,
        string contentType = "text/plain", string extraHeaders = "")
    {
        var statusText = statusCode switch
        {
            200 => "OK",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error"
        };

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var response = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\n{extraHeaders}\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(response);

        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
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
        catch { }

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

    private sealed class SingletonServiceProvider(VisualStudioExtensibility extensibility) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(VisualStudioExtensibility) ? extensibility : null;
    }
}
