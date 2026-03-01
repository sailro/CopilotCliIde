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
        var services = new SingletonServiceProvider(extensibility);
        var toolOptions = new McpServerToolCreateOptions { Services = services };
        var assembly = typeof(McpPipeServer).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null)
                continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() == null)
                    continue;

                try
                {
                    var tool = McpServerTool.Create(method, options: toolOptions);
                    _serverOptions.ToolCollection.Add(tool);
                }
                catch (Exception ex)
                {
                    await LogAsync($"Failed to register tool {type.Name}.{method.Name}: {ex.Message}").ConfigureAwait(false);
                }
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
    /// over the pipe. We use StreamableHttpServerTransport to handle the MCP session properly.
    /// </summary>
    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        StreamableHttpServerTransport? transport = null;
        McpServer? server = null;

        try
        {
            var serviceProvider = new SingletonServiceProvider(_extensibility!);
            transport = new StreamableHttpServerTransport { SessionId = $"vs-{Guid.NewGuid():N}" };
            server = McpServer.Create(transport, _serverOptions!, serviceProvider: serviceProvider);
            _ = server.RunAsync(ct);

            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var (method, path, headers, body) = await ReadHttpRequestAsync(pipe, ct).ConfigureAwait(false);
                if (method == null) break;

                headers.TryGetValue("authorization", out var auth);
                if (auth != $"Nonce {_nonce}")
                {
                    await WriteHttpResponseAsync(pipe, 401, "Unauthorized", ct).ConfigureAwait(false);
                    continue;
                }

                if (method == "POST" && (path == "/mcp" || path == "/"))
                {
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        await WriteHttpResponseAsync(pipe, 400, "Bad Request: empty body", ct).ConfigureAwait(false);
                        continue;
                    }

                    JsonRpcMessage? message;
                    try
                    {
                        message = JsonSerializer.Deserialize<JsonRpcMessage>(body);
                    }
                    catch (JsonException ex)
                    {
                        await LogAsync($"JSON parse error: {ex.Message} body='{body}'").ConfigureAwait(false);
                        await WriteHttpResponseAsync(pipe, 400, "Bad Request: invalid JSON", ct).ConfigureAwait(false);
                        continue;
                    }

                    if (message == null)
                    {
                        await WriteHttpResponseAsync(pipe, 400, "Bad Request", ct).ConfigureAwait(false);
                        continue;
                    }

                    var responseStream = new MemoryStream();

                    // HandlePostRequestAsync writes SSE events to the stream and returns
                    // true if there's a response to send back, false for notifications (202)
                    using var postCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    postCts.CancelAfter(TimeSpan.FromSeconds(30));

                    bool hasResponse;
                    try
                    {
                        hasResponse = await transport.HandlePostRequestAsync(message, responseStream, postCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        await WriteHttpResponseAsync(pipe, 504, "Timeout", ct).ConfigureAwait(false);
                        continue;
                    }

                    if (hasResponse && responseStream.Length > 0)
                    {
                        responseStream.Position = 0;
                        var responseBody = Encoding.UTF8.GetString(responseStream.ToArray());
                        await WriteHttpResponseAsync(pipe, 200, responseBody, ct,
                            contentType: "text/event-stream",
                            extraHeaders: $"Mcp-Session-Id: {transport.SessionId}\r\n").ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteHttpResponseAsync(pipe, 202, "", ct,
                            extraHeaders: $"Mcp-Session-Id: {transport.SessionId}\r\n").ConfigureAwait(false);
                    }
                    continue;
                }

                if (method == "GET" && (path == "/mcp" || path == "/"))
                {
                    await WriteHttpResponseAsync(pipe, 405, "Method Not Allowed", ct).ConfigureAwait(false);
                    continue;
                }

                if (method == "DELETE" && (path == "/mcp" || path == "/"))
                {
                    await WriteHttpResponseAsync(pipe, 200, "", ct).ConfigureAwait(false);
                    break;
                }

                await WriteHttpResponseAsync(pipe, 404, "Not Found", ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Log connection-level errors
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".copilot", "ide", "vs-connection.log");
                await File.AppendAllTextAsync(logPath, $"{DateTime.UtcNow:O} {ex}\n\n").ConfigureAwait(false);
            }
            catch { }
        }
        finally
        {
            if (transport != null) await transport.DisposeAsync().ConfigureAwait(false);
            if (server != null) await server.DisposeAsync().ConfigureAwait(false);
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
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

        // Read body
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
        else if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            // Read chunked transfer encoding
            body = await ReadChunkedBodyAsync(stream, ct).ConfigureAwait(false);
        }

        return (method, path, headers, body);
    }

    private static async Task<string> ReadChunkedBodyAsync(Stream stream, CancellationToken ct)
    {
        var result = new StringBuilder();
        var lineBuf = new StringBuilder();

        while (true)
        {
            // Read chunk size line (hex\r\n)
            lineBuf.Clear();
            while (true)
            {
                var b = new byte[1];
                var read = await stream.ReadAsync(b, ct).ConfigureAwait(false);
                if (read == 0) return result.ToString();
                lineBuf.Append((char)b[0]);
                if (lineBuf.Length >= 2 && lineBuf[^2] == '\r' && lineBuf[^1] == '\n')
                    break;
            }

            var sizeLine = lineBuf.ToString().TrimEnd('\r', '\n').Trim();
            // Strip chunk extensions if any
            var semiIdx = sizeLine.IndexOf(';');
            if (semiIdx >= 0) sizeLine = sizeLine[..semiIdx];

            if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, null, out var chunkSize) || chunkSize == 0)
                break;

            // Read chunk data
            var chunkBuf = new byte[chunkSize];
            var totalRead = 0;
            while (totalRead < chunkSize)
            {
                var read = await stream.ReadAsync(chunkBuf.AsMemory(totalRead, chunkSize - totalRead), ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }
            result.Append(Encoding.UTF8.GetString(chunkBuf, 0, totalRead));

            // Read trailing \r\n after chunk data
            var trail = new byte[2];
            await stream.ReadAsync(trail, ct).ConfigureAwait(false);
        }

        // Read trailing headers/\r\n after the final 0-size chunk
        var trailBuf = new byte[2];
        await stream.ReadAsync(trailBuf, ct).ConfigureAwait(false);

        return result.ToString();
    }

    private static async Task WriteHttpResponseAsync(Stream stream, int statusCode, string body, CancellationToken ct,
        string contentType = "text/plain", string extraHeaders = "")
    {
        var statusText = statusCode switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            504 => "Gateway Timeout",
            _ => "Error"
        };

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var response = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: keep-alive\r\n{extraHeaders}\r\n";
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

    private sealed class SingletonServiceProvider(VisualStudioExtensibility extensibility) : IServiceProvider, Microsoft.Extensions.DependencyInjection.IServiceProviderIsService
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(VisualStudioExtensibility) ? extensibility :
            serviceType == typeof(Microsoft.Extensions.DependencyInjection.IServiceProviderIsService) ? this :
            null;

        public bool IsService(Type serviceType) =>
            serviceType == typeof(VisualStudioExtensibility);
    }

    private static async Task LogAsync(string message)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".copilot", "ide", "vs-connection.log");
            await File.AppendAllTextAsync(logPath, $"{DateTime.UtcNow:O} {message}\n").ConfigureAwait(false);
        }
        catch { }
    }
}
