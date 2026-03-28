using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CopilotCliIde.Shared;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server;

public sealed class McpPipeServer : IAsyncDisposable
{
	private const int PipeStartupDelayMs = 200;
	private const int McpToolTimeoutSeconds = 30;

	private const string OpenDiffToolName = "open_diff";
	private const string ToolsCallMethodName = "tools/call";
	private const string SessionIdHeader = "mcp-session-id";
	private const string Crlf = "\r\n";
	private const string HttpOkStatusLine = "HTTP/1.1 200 OK";
	private const string SseContentTypeHeader = "content-type: text/event-stream";
	private const string SseCacheControlHeader = "cache-control: no-cache, no-transform";
	private const string ConnectionKeepAliveHeader = "connection: keep-alive";
	private const string TransferEncodingChunkedHeader = "transfer-encoding: chunked";
	private const string UnknownSessionIdValue = "none";

	private const string HttpMethodPost = "POST";
	private const string HttpMethodGet = "GET";
	private const string HttpMethodDelete = "DELETE";

	private const string McpRoute = "/mcp";
	private const string RootRoute = "/";

	private string? _nonce;
	private CancellationTokenSource? _cts;
	private McpServerOptions? _serverOptions;
	private RpcClient? _rpcClient;
	private readonly SseBroadcaster _broadcaster = new();

	public string? PipeName { get; private set; }

	public async Task StartAsync(RpcClient rpcClient, string pipeName, string nonce, CancellationToken ct)
	{
		_rpcClient = rpcClient;
		PipeName = pipeName;
		_nonce = nonce;
		_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

		_serverOptions = new McpServerOptions
		{
			ServerInfo = new Implementation { Name = "vscode-copilot-cli", Version = "0.0.1", Title = "VS Code Copilot CLI" },
			Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } },
			ToolCollection = []
		};

		// Scan assembly for [McpServerToolType] classes and their [McpServerTool] methods
		var services = new SingletonServiceProvider(rpcClient);
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
#pragma warning disable MCPEXP001
					tool.ProtocolTool.Execution = new ToolExecution { TaskSupport = ToolTaskSupport.Forbidden };
#pragma warning restore MCPEXP001
					_serverOptions.ToolCollection.Add(tool);
				}
				catch { /* Ignore */ }
			}
		}

		// Start accepting connections in background
		_ = Task.Run(() => AcceptConnectionsAsync(_cts.Token), ct);

		// Wait briefly for pipe to be created
		await Task.Delay(PipeStartupDelayMs, ct);
	}

	private async Task AcceptConnectionsAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			var pipe = new NamedPipeServerStream(
				PipeName!,
				PipeDirection.InOut,
				NamedPipeServerStream.MaxAllowedServerInstances,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous);

			try
			{
				await pipe.WaitForConnectionAsync(ct);
				_ = Task.Run(() => HandleConnectionAsync(pipe, ct), ct);
			}
			catch (OperationCanceledException)
			{
				await pipe.DisposeAsync();
				break;
			}
			catch
			{
				await pipe.DisposeAsync();
			}
		}
	}

	private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
	{
		StreamableHttpServerTransport? transport = null;
		McpServer? server = null;

		try
		{
			var serviceProvider = new SingletonServiceProvider(_rpcClient!);
			transport = new StreamableHttpServerTransport { SessionId = $"vs-{Guid.NewGuid():N}" };
			server = McpServer.Create(transport, _serverOptions!, serviceProvider: serviceProvider);
			_ = server.RunAsync(ct);

			while (pipe.IsConnected && !ct.IsCancellationRequested)
			{
				var (method, path, headers, body) = await HttpPipeFraming.ReadHttpRequestAsync(pipe, ct);
				if (method == null) break;

				headers.TryGetValue("authorization", out var auth);
				if (auth != $"Nonce {_nonce}")
				{
					await HttpPipeFraming.WriteHttpResponseAsync(pipe, 401, "Unauthorized", ct);
					continue;
				}

				var isMcpRoute = path is McpRoute or RootRoute;
				if (isMcpRoute)
				{
					switch (method)
					{
						case HttpMethodPost:
							await HandleMcpPostAsync(pipe, body, transport, ct);
							continue;
						case HttpMethodGet:
							await HandleSseGetAsync(pipe, headers, transport, ct);
							break;
						case HttpMethodDelete:
							await HandleMcpDeleteAsync(pipe, ct);
							break;
					}
				}

				await HttpPipeFraming.WriteHttpResponseAsync(pipe, 404, "Not Found", ct);
			}
		}
		catch (OperationCanceledException) { }
		catch { /* Ignore */ }
		finally
		{
			if (transport != null) await transport.DisposeAsync();
			if (server != null) await server.DisposeAsync();
			await pipe.DisposeAsync();
		}
	}

	private static async Task HandleMcpPostAsync(NamedPipeServerStream pipe, string? body,
		StreamableHttpServerTransport transport, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			await HttpPipeFraming.WriteHttpResponseAsync(pipe, 400, "Bad Request: empty body", ct);
			return;
		}

		JsonRpcMessage? message;
		try
		{
			message = JsonSerializer.Deserialize<JsonRpcMessage>(body);
		}
		catch (JsonException)
		{
			await HttpPipeFraming.WriteHttpResponseAsync(pipe, 400, "Bad Request: invalid JSON", ct);
			return;
		}

		if (message == null)
		{
			await HttpPipeFraming.WriteHttpResponseAsync(pipe, 400, "Bad Request", ct);
			return;
		}

		var responseStream = new MemoryStream();

		// Determine timeout — open_diff blocks until user accepts/rejects
		var isOpenDiff = false;
		try
		{
			using var jsonDoc = JsonDocument.Parse(body);
			if (jsonDoc.RootElement.TryGetProperty("method", out var m) &&
				m.GetString() == ToolsCallMethodName &&
				jsonDoc.RootElement.TryGetProperty("params", out var p) &&
				p.TryGetProperty("name", out var n) &&
				n.GetString() == OpenDiffToolName)
			{
				isOpenDiff = true;
			}
		}
		catch { /* Ignore */ }

		using var postCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		if (!isOpenDiff)
			postCts.CancelAfter(TimeSpan.FromSeconds(McpToolTimeoutSeconds));

		bool hasResponse;
		try
		{
			hasResponse = await transport.HandlePostRequestAsync(message, responseStream, postCts.Token);
		}
		catch (OperationCanceledException)
		{
			// Use parent ct — postCts may have timed out
			await HttpPipeFraming.WriteHttpResponseAsync(pipe, 504, "Timeout", ct);
			return;
		}
		catch (Exception ex)
		{
			// Use parent ct — postCts may have timed out
			await HttpPipeFraming.WriteHttpResponseAsync(pipe, 500, ex.Message, ct);
			return;
		}

		if (hasResponse && responseStream.Length > 0)
		{
			responseStream.Position = 0;
			var responseBody = Encoding.UTF8.GetString(responseStream.ToArray());
			await HttpPipeFraming.WriteHttpResponseAsync(pipe, 200, responseBody, postCts.Token,
				contentType: "text/event-stream",
				extraHeaders: $"{SessionIdHeader}: {transport.SessionId}{Crlf}");
		}
		else
		{
			await HttpPipeFraming.WriteHttpResponseAsync(pipe, 202, "", postCts.Token,
				extraHeaders: $"{SessionIdHeader}: {transport.SessionId}{Crlf}");
		}
	}

	private async Task HandleSseGetAsync(NamedPipeServerStream pipe, Dictionary<string, string> headers,
		StreamableHttpServerTransport transport, CancellationToken ct)
	{
		// SSE stream for server-to-client notifications
		headers.TryGetValue(SessionIdHeader, out var sseSessionId);
		var sseHeaders = BuildSseHeaders(sseSessionId ?? transport.SessionId ?? UnknownSessionIdValue);
		await pipe.WriteAsync(Encoding.UTF8.GetBytes(sseHeaders), ct);
		await pipe.FlushAsync(ct);

		var sseClient = new SseClient(pipe);
		_broadcaster.AddClient(sseClient);

		// Reset dedup state in VS and push the current selection
		// and diagnostics so copilot-cli has the right state
		// immediately (the VS extension may have pushed earlier
		// when no SSE clients were connected yet)
		_ = Task.Run(async () => await PushInitialStateAsync(), ct);

		try
		{
			// Keep connection alive until cancelled or pipe breaks
			await sseClient.WaitAsync(ct);
		}
		finally
		{
			_broadcaster.RemoveClient(sseClient);
		}
	}

	private static async Task HandleMcpDeleteAsync(NamedPipeServerStream pipe, CancellationToken ct)
	{
		await HttpPipeFraming.WriteHttpResponseAsync(pipe, 200, "", ct);
	}

	private static string BuildSseHeaders(string sessionId)
	{
		return string.Concat(
			HttpOkStatusLine, Crlf,
			SseContentTypeHeader, Crlf,
			SseCacheControlHeader, Crlf,
			ConnectionKeepAliveHeader, Crlf,
			SessionIdHeader, ": ", sessionId, Crlf,
			TransferEncodingChunkedHeader, Crlf,
			Crlf);
	}

	public ValueTask DisposeAsync()
	{
		if (_cts == null)
			return ValueTask.CompletedTask;

		_cts.Cancel();
		_cts.Dispose();
		_cts = null;
		return ValueTask.CompletedTask;
	}

	// Resets VS dedup state and pushes current selection + diagnostics to the new SSE client.
	private async Task PushInitialStateAsync()
	{
		try { await _rpcClient!.VsServices!.ResetNotificationStateAsync(); }
		catch { /* VS not ready */ }

		await PushCurrentSelectionAsync();
		await PushCurrentDiagnosticsAsync();
	}

	private async Task PushCurrentSelectionAsync()
	{
		try
		{
			var sel = await _rpcClient!.VsServices!.GetSelectionAsync();
			if (string.IsNullOrEmpty(sel.FilePath)) return;

			await PushSelectionChangedAsync(new SelectionNotification
			{
				Text = sel.Text,
				FilePath = sel.FilePath,
				FileUrl = sel.FileUrl,
				Selection = sel.Selection
			});
		}
		catch { /* VS not ready or no active editor — nothing to push */ }
	}

	private async Task PushCurrentDiagnosticsAsync()
	{
		try
		{
			var diag = await _rpcClient!.VsServices!.GetDiagnosticsAsync(null);
			if (diag.Files == null || diag.Files.Count == 0) return;

			await PushDiagnosticsChangedAsync(new DiagnosticsChangedNotification
			{
				Uris = [.. diag.Files.Select(f => new DiagnosticsChangedUri
				{
					Uri = f.Uri,
					Diagnostics = f.Diagnostics
				})]
			});
		}
		catch { /* VS not ready or no diagnostics */ }
	}

	public Task PushSelectionChangedAsync(SelectionNotification notification)
	{
		return _broadcaster.BroadcastSelectionChangedAsync(notification);
	}

	public Task PushDiagnosticsChangedAsync(DiagnosticsChangedNotification notification)
	{
		return _broadcaster.BroadcastDiagnosticsChangedAsync(notification);
	}

	public Task PushNotificationAsync(string method, object? @params)
	{
		return _broadcaster.BroadcastAsync(method, @params);
	}
}

