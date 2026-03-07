using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CopilotCliIde.Shared;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server;

/// <summary>
/// Hosts an MCP server on a Windows named pipe so Copilot CLI can connect via /ide.
/// Copilot CLI connects via HTTP (Streamable HTTP MCP transport) over the named pipe.
/// We use a raw HTTP listener on the named pipe to handle requests.
/// </summary>
public sealed class McpPipeServer : IAsyncDisposable
{
	private string? _pipeName;
	private string? _nonce;
	private CancellationTokenSource? _cts;
	private McpServerOptions? _serverOptions;
	private RpcClient? _rpcClient;
	private readonly List<SseClient> _sseClients = [];
	private readonly Lock _sseClientsLock = new();

	public string? PipeName => _pipeName;

	public async Task StartAsync(RpcClient rpcClient, string pipeName, string nonce, CancellationToken ct)
	{
		_rpcClient = rpcClient;
		_pipeName = pipeName;
		_nonce = nonce;
		_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

		_serverOptions = new McpServerOptions
		{
			ServerInfo = new Implementation { Name = "vscode-copilot-cli", Version = "1.0.0", Title = "VS Code Copilot CLI" },
			Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } },
			ToolCollection = [],
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
		await Task.Delay(200, ct);
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
			var serviceProvider = new SingletonServiceProvider(_rpcClient!);
			transport = new StreamableHttpServerTransport { SessionId = $"vs-{Guid.NewGuid():N}" };
			server = McpServer.Create(transport, _serverOptions!, serviceProvider: serviceProvider);
			_ = server.RunAsync(ct);

			while (pipe.IsConnected && !ct.IsCancellationRequested)
			{
				var (method, path, headers, body) = await ReadHttpRequestAsync(pipe, ct);
				if (method == null) break;

				headers.TryGetValue("authorization", out var auth);
				if (auth != $"Nonce {_nonce}")
				{
					await WriteHttpResponseAsync(pipe, 401, "Unauthorized", ct);
					continue;
				}

				if (method == "POST" && (path == "/mcp" || path == "/"))
				{
					if (string.IsNullOrWhiteSpace(body))
					{
						await WriteHttpResponseAsync(pipe, 400, "Bad Request: empty body", ct);
						continue;
					}

					JsonRpcMessage? message;
					try
					{
						message = JsonSerializer.Deserialize<JsonRpcMessage>(body);
					}
					catch (JsonException)
					{
						await WriteHttpResponseAsync(pipe, 400, "Bad Request: invalid JSON", ct);
						continue;
					}

					if (message == null)
					{
						await WriteHttpResponseAsync(pipe, 400, "Bad Request", ct);
						continue;
					}

					var responseStream = new MemoryStream();

					// HandlePostRequestAsync writes SSE events to the stream and returns
					// true if there's a response to send back, false for notifications (202)
					// Determine timeout — open_diff blocks until user accepts/rejects
					var isOpenDiff = false;
					try
					{
						using var jsonDoc = JsonDocument.Parse(body);
						if (jsonDoc.RootElement.TryGetProperty("method", out var m) &&
							m.GetString() == "tools/call" &&
							jsonDoc.RootElement.TryGetProperty("params", out var p) &&
							p.TryGetProperty("name", out var n) &&
							n.GetString() == "open_diff")
						{
							isOpenDiff = true;
						}
					}
					catch { /* Ignore */ }

					using var postCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
					if (!isOpenDiff)
						postCts.CancelAfter(TimeSpan.FromSeconds(30));

					bool hasResponse;
					try
					{
						hasResponse = await transport.HandlePostRequestAsync(message, responseStream, postCts.Token);
					}
					catch (OperationCanceledException)
					{
						await WriteHttpResponseAsync(pipe, 504, "Timeout", ct);
						continue;
					}
					catch (Exception ex)
					{
						await WriteHttpResponseAsync(pipe, 500, ex.Message, ct);
						continue;
					}

					if (hasResponse && responseStream.Length > 0)
					{
						responseStream.Position = 0;
						var responseBody = Encoding.UTF8.GetString(responseStream.ToArray());
						await WriteHttpResponseAsync(pipe, 200, responseBody, postCts.Token,
							contentType: "text/event-stream",
							extraHeaders: $"Mcp-Session-Id: {transport.SessionId}\r\n");
					}
					else
					{
						await WriteHttpResponseAsync(pipe, 202, "", postCts.Token,
							extraHeaders: $"Mcp-Session-Id: {transport.SessionId}\r\n");
					}
					continue;
				}

				if (method == "GET" && path is "/mcp" or "/")
				{
					// SSE stream for server-to-client notifications
					headers.TryGetValue("mcp-session-id", out var sseSessionId);
					var sseHeaders = $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache, no-transform\r\nConnection: keep-alive\r\nMcp-Session-Id: {sseSessionId ?? transport?.SessionId ?? "none"}\r\nTransfer-Encoding: chunked\r\n\r\n";
					await pipe.WriteAsync(Encoding.UTF8.GetBytes(sseHeaders), ct);
					await pipe.FlushAsync(ct);

					var sseClient = new SseClient(pipe);
					lock (_sseClientsLock) { _sseClients.Add(sseClient); }

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
						lock (_sseClientsLock) { _sseClients.Remove(sseClient); }
					}
					break;
				}

				if (method == "DELETE" && path is "/mcp" or "/")
				{
					await WriteHttpResponseAsync(pipe, 200, "", ct);
					break;
				}

				await WriteHttpResponseAsync(pipe, 404, "Not Found", ct);
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

	internal static async Task<(string? method, string? path, Dictionary<string, string> headers, string body)> ReadHttpRequestAsync(Stream stream, CancellationToken ct)
	{
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var sb = new StringBuilder();
		var buffer = new byte[1];
		var headerComplete = false;

		// Read headers byte by byte until \r\n\r\n
		while (!headerComplete && !ct.IsCancellationRequested)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
			if (read == 0) return (null, null, headers, "");

			sb.Append((char)buffer[0]);
			if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
				headerComplete = true;
		}

		var headerText = sb.ToString();
		var lines = headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);
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
				var read = await stream.ReadAsync(bodyBuffer.AsMemory(totalRead, contentLength - totalRead), ct);
				if (read == 0) break;
				totalRead += read;
			}
			body = Encoding.UTF8.GetString(bodyBuffer, 0, totalRead);
		}
		else if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
		{
			// Read chunked transfer encoding
			body = await ReadChunkedBodyAsync(stream, ct);
		}

		return (method, path, headers, body);
	}

	internal static async Task<string> ReadChunkedBodyAsync(Stream stream, CancellationToken ct)
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
				var read = await stream.ReadAsync(b.AsMemory(0, 1), ct);
				if (read == 0) return result.ToString();
				lineBuf.Append((char)b[0]);
				if (lineBuf is [.., '\r', '\n'])
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
				var read = await stream.ReadAsync(chunkBuf.AsMemory(totalRead, chunkSize - totalRead), ct);
				if (read == 0) break;
				totalRead += read;
			}
			result.Append(Encoding.UTF8.GetString(chunkBuf, 0, totalRead));

			// Read trailing \r\n after chunk data
			var trail = new byte[2];
			await stream.ReadExactlyAsync(trail.AsMemory(0, 2), ct);
		}

		// Read trailing headers/\r\n after the final 0-size chunk
		var trailBuf = new byte[2];
		await stream.ReadExactlyAsync(trailBuf.AsMemory(0, 2), ct);

		return result.ToString();
	}

	internal static async Task WriteHttpResponseAsync(Stream stream, int statusCode, string body, CancellationToken ct,
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

		await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct);
		await stream.WriteAsync(bodyBytes.AsMemory(0, bodyBytes.Length), ct);
		await stream.FlushAsync(ct);
	}

	public ValueTask DisposeAsync()
	{
		if (_cts != null)
		{
			_cts.Cancel();
			_cts.Dispose();
			_cts = null;
		}
		return ValueTask.CompletedTask;
	}

	/// <summary>
	/// Resets notification dedup state in VS and pushes the current selection
	/// and diagnostics to the newly connected SSE client. The dedup reset
	/// ensures VS re-sends events even if the content hasn't changed since
	/// the previous CLI session.
	/// </summary>
	private async Task PushInitialStateAsync()
	{
		try { await _rpcClient!.VsServices!.ResetNotificationStateAsync(); }
		catch { /* VS not ready */ }

		await PushCurrentSelectionAsync();
		await PushCurrentDiagnosticsAsync();
	}

	/// <summary>
	/// Fetches the current selection from VS via RPC and pushes it to all
	/// connected SSE clients. Called when a new SSE client connects so
	/// copilot-cli immediately shows the active file.
	/// </summary>
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

	/// <summary>
	/// Fetches the current diagnostics from VS via RPC and pushes them to all
	/// connected SSE clients. Called when a new SSE client connects so
	/// copilot-cli has the current Error List state immediately.
	/// </summary>
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

	/// <summary>
	/// Formats and pushes a selection_changed notification to all connected SSE clients.
	/// Used by both the initial push on connect and the real-time event forwarding.
	/// </summary>
	public Task PushSelectionChangedAsync(SelectionNotification notification)
	{
		return PushNotificationAsync("selection_changed", new
		{
			text = notification.Text ?? "",
			filePath = notification.FilePath,
			fileUrl = notification.FileUrl,
			selection = notification.Selection == null ? null : new
			{
				start = new { line = notification.Selection.Start?.Line ?? 0, character = notification.Selection.Start?.Character ?? 0 },
				end = new { line = notification.Selection.End?.Line ?? 0, character = notification.Selection.End?.Character ?? 0 },
				isEmpty = notification.Selection.IsEmpty
			}
		});
	}

	/// <summary>
	/// Formats and pushes a diagnostics_changed notification to all connected SSE clients.
	/// Used by both the initial push on connect and the real-time event forwarding.
	/// </summary>
	public Task PushDiagnosticsChangedAsync(DiagnosticsChangedNotification notification)
	{
		return PushNotificationAsync("diagnostics_changed", new
		{
			uris = notification.Uris?.Select(u => new
			{
				uri = u.Uri,
				diagnostics = u.Diagnostics?.Select(d => new
				{
					range = d.Range == null ? null : new
					{
						start = new { line = d.Range.Start?.Line ?? 0, character = d.Range.Start?.Character ?? 0 },
						end = new { line = d.Range.End?.Line ?? 0, character = d.Range.End?.Character ?? 0 }
					},
					message = d.Message,
					severity = d.Severity,
					code = d.Code
				})
			})
		});
	}

	/// <summary>
	/// Pushes a JSON-RPC notification to all connected SSE clients.
	/// </summary>
	public async Task PushNotificationAsync(string method, object? @params)
	{
		var notification = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params });
		var sseEvent = $"event: message\ndata: {notification}\n\n";
		var chunkData = Encoding.UTF8.GetBytes(sseEvent);
		var chunk = Encoding.UTF8.GetBytes($"{chunkData.Length:x}\r\n");
		var chunkEnd = Encoding.UTF8.GetBytes("\r\n");

		SseClient[] clients;
		lock (_sseClientsLock) { clients = [.. _sseClients]; }

		foreach (var client in clients)
		{
			try
			{
				await client.Pipe.WriteAsync(chunk);
				await client.Pipe.WriteAsync(chunkData);
				await client.Pipe.WriteAsync(chunkEnd);
				await client.Pipe.FlushAsync();
			}
			catch
			{
				client.Close();
			}
		}
	}

	private sealed class SseClient(NamedPipeServerStream pipe)
	{
		private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public NamedPipeServerStream Pipe => pipe;
		public Task WaitAsync(CancellationToken ct)
		{
			ct.Register(() => _done.TrySetResult());
			return _done.Task;
		}
		public void Close() => _done.TrySetResult();
	}

	private sealed class SingletonServiceProvider(RpcClient rpcClient) : IServiceProvider, Microsoft.Extensions.DependencyInjection.IServiceProviderIsService
	{
		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(RpcClient))
				return rpcClient;
			if (serviceType == typeof(Microsoft.Extensions.DependencyInjection.IServiceProviderIsService))
				return this;
			return null;
		}

		public bool IsService(Type serviceType)
		{
			return serviceType == typeof(RpcClient);
		}
	}
}

