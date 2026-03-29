using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CopilotCliIde.Shared;
using NSubstitute;

namespace CopilotCliIde.Server.Tests;

public class SseNotificationIntegrationTests
{
	#region Helpers — Server bootstrap

	/// <summary>
	/// Boots a fresh MCP server, completes the handshake, returns the session ID.
	/// Optionally configures the mock VS services before startup.
	/// </summary>
	private static async Task<(AspNetMcpPipeServer Server, string PipeName, string Nonce, string SessionId)>
		BootServerAsync(CancellationToken ct, Action<IVsServiceRpc>? configureMock = null)
	{
		var mockVsServices = Substitute.For<IVsServiceRpc>();
		configureMock?.Invoke(mockVsServices);
		var rpcClient = new RpcClient(mockVsServices);

		var pipeName = $"copilot-sse-test-{Guid.NewGuid():N}";
		const string nonce = "test-nonce";

		var server = new AspNetMcpPipeServer();
		await server.StartAsync(rpcClient, pipeName, nonce, ct);

		var initRequest = JsonSerializer.Serialize(new
		{
			method = "initialize",
			@params = new
			{
				protocolVersion = "2025-11-25",
				capabilities = new { },
				clientInfo = new { name = "sse-test", version = "1.0.0" }
			},
			jsonrpc = "2.0",
			id = 0
		});

		var (_, sessionId) = await SendHttpPostOnNewPipeAsync(pipeName, initRequest, nonce, null, ct);
		Assert.False(string.IsNullOrWhiteSpace(sessionId));

		var initializedRequest = JsonSerializer.Serialize(new { method = "notifications/initialized", jsonrpc = "2.0" });
		await SendHttpPostOnNewPipeAsync(pipeName, initializedRequest, nonce, sessionId, ct);

		return (server, pipeName, nonce, sessionId!);
	}

	private static SelectionNotification MakeSelection(string text, int line = 1) => new()
	{
		Text = text,
		FilePath = @"C:\Dev\vsext\src\CopilotCliIde\Program.cs",
		FileUrl = "file:///C:/Dev/vsext/src/CopilotCliIde/Program.cs",
		Selection = new SelectionRange
		{
			Start = new SelectionPosition { Line = line, Character = 0 },
			End = new SelectionPosition { Line = line, Character = text.Length },
			IsEmpty = false
		}
	};

	private static DiagnosticsChangedNotification MakeDiagnostics(string message = "CS0001: test error") => new()
	{
		Uris =
		[
			new DiagnosticsChangedUri
			{
				Uri = "file:///C:/Dev/vsext/src/test.cs",
				Diagnostics =
				[
					new DiagnosticItem
					{
						Message = message,
						Severity = DiagnosticSeverity.Error,
						Code = "CS0001",
						Range = new DiagnosticRange
						{
							Start = new SelectionPosition { Line = 1, Character = 0 },
							End = new SelectionPosition { Line = 1, Character = 10 }
						}
					}
				]
			}
		]
	};

	#endregion

	#region Live push visibility

	[Fact]
	public async Task SelectionChanged_IsDeliveredOnOpenGetMcpStream()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token);
		await using (server)
		{
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			await server.PushSelectionChangedAsync(MakeSelection("hello"));

			var payload = await ReadUntilContainsAsync(ssePipe, "\"method\":\"selection_changed\"", cts.Token);
			AssertSseMessageEnvelope(payload, "selection_changed");
			Assert.Contains("\"text\":\"hello\"", payload);
		}
	}

	[Fact]
	public async Task SelectionChanged_StillDeliveredAfterRegularPostTraffic()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token);
		await using (server)
		{
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			// Interleave a POST (tools/list) before pushing the notification
			var toolsListRequest = JsonSerializer.Serialize(new { method = "tools/list", jsonrpc = "2.0", id = 1 });
			await SendHttpPostOnNewPipeAsync(pipeName, toolsListRequest, nonce, sessionId, cts.Token);

			await server.PushSelectionChangedAsync(MakeSelection("after-post", line: 2));

			var payload = await ReadUntilContainsAsync(ssePipe, "\"text\":\"after-post\"", cts.Token);
			AssertSseMessageEnvelope(payload, "selection_changed");
		}
	}

	[Fact]
	public async Task DiagnosticsChanged_IsDeliveredOnOpenSseStream()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token);
		await using (server)
		{
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			await server.PushDiagnosticsChangedAsync(MakeDiagnostics("CS0001: missing semicolon"));

			var payload = await ReadUntilContainsAsync(ssePipe, "\"method\":\"diagnostics_changed\"", cts.Token);
			AssertSseMessageEnvelope(payload, "diagnostics_changed");
			Assert.Contains("CS0001: missing semicolon", payload);
		}
	}

	[Fact]
	public async Task MultiplePushes_AllDeliveredInOrder()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token);
		await using (server)
		{
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			// Push selection, then diagnostics
			await server.PushSelectionChangedAsync(MakeSelection("first-push"));
			await server.PushDiagnosticsChangedAsync(MakeDiagnostics("second-push-diag"));

			// Read until both arrive — the second one guarantees the first was also delivered
			var payload = await ReadUntilContainsAsync(ssePipe, "second-push-diag", cts.Token);
			Assert.Contains("\"method\":\"selection_changed\"", payload);
			Assert.Contains("\"method\":\"diagnostics_changed\"", payload);

			// Verify ordering: selection_changed should appear before diagnostics_changed
			var selIdx = payload.IndexOf("\"method\":\"selection_changed\"", StringComparison.Ordinal);
			var diagIdx = payload.IndexOf("\"method\":\"diagnostics_changed\"", StringComparison.Ordinal);
			Assert.True(selIdx < diagIdx, "selection_changed should arrive before diagnostics_changed");
		}
	}

	#endregion

	#region Initial push on connect

	[Fact]
	public async Task InitialState_SelectionPushedOnSseConnect()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token, mock =>
		{
			mock.GetSelectionAsync().Returns(new SelectionResult
			{
				Current = true,
				FilePath = @"C:\Dev\vsext\src\initial.cs",
				FileUrl = "file:///C:/Dev/vsext/src/initial.cs",
				Text = "initial-selection-text",
				Selection = new SelectionRange
				{
					Start = new SelectionPosition { Line = 5, Character = 0 },
					End = new SelectionPosition { Line = 5, Character = 21 },
					IsEmpty = false
				}
			});
			mock.GetDiagnosticsAsync(null).Returns(new DiagnosticsResult { Files = [] });
		});

		await using (server)
		{
			// Opening the SSE stream triggers PushInitialStateAsync — no explicit push call needed
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			var payload = await ReadUntilContainsAsync(ssePipe, "initial-selection-text", cts.Token);
			AssertSseMessageEnvelope(payload, "selection_changed");
			Assert.Contains("initial.cs", payload);
		}
	}

	[Fact]
	public async Task InitialState_DiagnosticsPushedOnSseConnect()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token, mock =>
		{
			mock.GetSelectionAsync().Returns(new SelectionResult { FilePath = null });
			mock.GetDiagnosticsAsync(null).Returns(new DiagnosticsResult
			{
				Files =
				[
					new FileDiagnostics
					{
						Uri = "file:///C:/Dev/vsext/src/broken.cs",
						Diagnostics =
						[
							new DiagnosticItem
							{
								Message = "initial-diag-message",
								Severity = DiagnosticSeverity.Warning,
								Code = "CS8600",
								Range = new DiagnosticRange
								{
									Start = new SelectionPosition { Line = 10, Character = 0 },
									End = new SelectionPosition { Line = 10, Character = 20 }
								}
							}
						]
					}
				]
			});
		});

		await using (server)
		{
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			var payload = await ReadUntilContainsAsync(ssePipe, "initial-diag-message", cts.Token);
			AssertSseMessageEnvelope(payload, "diagnostics_changed");
			Assert.Contains("CS8600", payload);
		}
	}

	[Fact]
	public async Task InitialState_BothSelectionAndDiagnosticsPushedOnConnect()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token, mock =>
		{
			mock.GetSelectionAsync().Returns(new SelectionResult
			{
				Current = true,
				FilePath = @"C:\Dev\vsext\src\both.cs",
				FileUrl = "file:///C:/Dev/vsext/src/both.cs",
				Text = "both-sel",
				Selection = new SelectionRange
				{
					Start = new SelectionPosition { Line = 1, Character = 0 },
					End = new SelectionPosition { Line = 1, Character = 8 },
					IsEmpty = false
				}
			});
			mock.GetDiagnosticsAsync(null).Returns(new DiagnosticsResult
			{
				Files =
				[
					new FileDiagnostics
					{
						Uri = "file:///C:/Dev/vsext/src/both.cs",
						Diagnostics =
						[
							new DiagnosticItem { Message = "both-diag", Severity = DiagnosticSeverity.Error, Code = "CS0000" }
						]
					}
				]
			});
		});

		await using (server)
		{
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			// Wait for diagnostics (pushed after selection) — confirms both arrived
			var payload = await ReadUntilContainsAsync(ssePipe, "both-diag", cts.Token);
			Assert.Contains("\"method\":\"selection_changed\"", payload);
			Assert.Contains("\"method\":\"diagnostics_changed\"", payload);
			Assert.Contains("both-sel", payload);
		}
	}

	[Fact]
	public async Task InitialState_NoSelectionPushed_WhenNoActiveEditor()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token, mock =>
		{
			// Empty file path → no editor open
			mock.GetSelectionAsync().Returns(new SelectionResult { FilePath = "" });
			mock.GetDiagnosticsAsync(null).Returns(new DiagnosticsResult { Files = [] });
		});

		await using (server)
		{
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			// Push an explicit selection to prove the stream works
			await server.PushSelectionChangedAsync(MakeSelection("explicit-push"));

			var payload = await ReadUntilContainsAsync(ssePipe, "explicit-push", cts.Token);
			// Only the explicit push should be present — no initial push since FilePath was empty
			var selectionCount = CountOccurrences(payload, "\"method\":\"selection_changed\"");
			Assert.Equal(1, selectionCount);
		}
	}

	#endregion

	#region SSE event IDs (resume contract)

	[Fact]
	public async Task SseEvents_ContainStructuredEventIds()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token);
		await using (server)
		{
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			await server.PushSelectionChangedAsync(MakeSelection("id-test"));

			var payload = await ReadUntilContainsAsync(ssePipe, "id-test", cts.Token);

			// Each SSE event should have an id: line with sessionId:streamId:sequence format
			var eventIds = ExtractSseEventIds(payload);
			Assert.NotEmpty(eventIds);

			foreach (var eventId in eventIds)
			{
				var parts = eventId.Split(':');
				Assert.Equal(3, parts.Length);
				Assert.False(string.IsNullOrWhiteSpace(parts[0]), "sessionId segment");
				Assert.False(string.IsNullOrWhiteSpace(parts[1]), "streamId segment");
				Assert.True(long.TryParse(parts[2], out var seq), $"sequence should be numeric, got: {parts[2]}");
				Assert.True(seq > 0, "sequence should be positive");
			}
		}
	}

	[Fact]
	public async Task SseEventIds_AreMonotonicallyIncreasing()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token);
		await using (server)
		{
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			// Push two events to get at least 2 IDs
			await server.PushSelectionChangedAsync(MakeSelection("mono-1"));
			await server.PushDiagnosticsChangedAsync(MakeDiagnostics("mono-2"));

			var payload = await ReadUntilContainsAsync(ssePipe, "mono-2", cts.Token);
			var eventIds = ExtractSseEventIds(payload);

			// Verify sequences are strictly increasing
			var sequences = eventIds.Select(id => long.Parse(id.Split(':')[2])).ToList();
			for (var i = 1; i < sequences.Count; i++)
			{
				Assert.True(sequences[i] > sequences[i - 1],
					$"Event IDs not monotonically increasing: {sequences[i - 1]} then {sequences[i]}");
			}
		}
	}

	[Fact]
	public async Task Resume_ReplaysMissedEvents_WhenLastEventIdProvided()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token);
		await using (server)
		{
			// Phase 1: Open SSE, push events, capture event IDs
			string lastEventId;
			await using (var ssePipe1 = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token))
			{
				await server.PushSelectionChangedAsync(MakeSelection("before-disconnect"));
				var payload1 = await ReadUntilContainsAsync(ssePipe1, "before-disconnect", cts.Token);

				var ids = ExtractSseEventIds(payload1);
				Assert.NotEmpty(ids);
				lastEventId = ids.Last();
			}

			// Phase 2: Push while disconnected — these events go to the store history
			await server.PushSelectionChangedAsync(MakeSelection("missed-while-away"));
			await server.PushDiagnosticsChangedAsync(MakeDiagnostics("also-missed"));

			// Phase 3: Reconnect with Last-Event-ID — expect missed events replayed
			await using var ssePipe2 = await OpenSseGetStreamWithLastEventIdAsync(
				pipeName, nonce, sessionId, lastEventId, cts.Token);

			var payload2 = await ReadUntilContainsAsync(ssePipe2, "also-missed", cts.Token);
			Assert.Contains("missed-while-away", payload2);
			Assert.Contains("also-missed", payload2);
		}
	}

	#endregion

	#region Transport robustness

	[Fact]
	public async Task SsePayload_IsValidJsonWithinDataLine()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var (server, pipeName, nonce, sessionId) = await BootServerAsync(cts.Token);
		await using (server)
		{
			await using var ssePipe = await OpenSseGetStreamAsync(pipeName, nonce, sessionId, cts.Token);

			await server.PushSelectionChangedAsync(MakeSelection("json-validate"));

			var payload = await ReadUntilContainsAsync(ssePipe, "json-validate", cts.Token);

			// Extract the data: line and verify it parses as valid JSON-RPC
			var dataJson = ExtractSseDataJson(payload, "selection_changed");
			Assert.NotNull(dataJson);
			var doc = JsonDocument.Parse(dataJson);
			Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
			Assert.Equal("selection_changed", doc.RootElement.GetProperty("method").GetString());
			Assert.True(doc.RootElement.TryGetProperty("params", out _), "notification must have params");
		}
	}

	#endregion

	private static void AssertSseMessageEnvelope(string payload, string expectedMethod)
	{
		var eventIndex = payload.IndexOf("event: message", StringComparison.Ordinal);
		Assert.True(eventIndex >= 0, $"Expected SSE event marker in payload:\n{payload}");

		var dataIndex = payload.IndexOf("data:", eventIndex, StringComparison.Ordinal);
		Assert.True(dataIndex >= 0, $"Expected SSE data marker after event marker in payload:\n{payload}");

		var dataLineEnd = payload.IndexOf('\n', dataIndex);
		if (dataLineEnd < 0)
		{
			dataLineEnd = payload.Length;
		}

		var dataLine = payload[dataIndex..dataLineEnd];
		Assert.Contains("\"jsonrpc\":\"2.0\"", dataLine, StringComparison.Ordinal);

		var methodJson = $"\"method\":\"{expectedMethod}\"";
		Assert.Contains(methodJson, dataLine, StringComparison.Ordinal);
	}

	private static async Task<NamedPipeClientStream> OpenSseGetStreamAsync(string pipeName, string nonce, string sessionId, CancellationToken ct)
	{
		var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await pipe.ConnectAsync(ct);

		var request =
			$"GET /mcp HTTP/1.1\r\nHost: localhost\r\nAuthorization: Nonce {nonce}\r\nAccept: text/event-stream\r\nmcp-protocol-version: 2025-11-25\r\nmcp-session-id: {sessionId}\r\nConnection: keep-alive\r\n\r\n";
		var bytes = Encoding.UTF8.GetBytes(request);
		await pipe.WriteAsync(bytes, ct);
		await pipe.FlushAsync(ct);

		var headers = await ReadHeadersAsync(pipe, ct);
		Assert.Contains("200", headers);
		Assert.Contains("text/event-stream", headers, StringComparison.OrdinalIgnoreCase);
		return pipe;
	}

	private static async Task<(string Body, string? SessionId)> SendHttpPostOnNewPipeAsync(
		string pipeName,
		string body,
		string nonce,
		string? sessionId,
		CancellationToken ct)
	{
		await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await pipe.ConnectAsync(ct);

		var bodyBytes = Encoding.UTF8.GetBytes(body);
		var sessionHeader = string.IsNullOrWhiteSpace(sessionId) ? "" : $"mcp-session-id: {sessionId}\r\n";
		var request =
			$"POST /mcp HTTP/1.1\r\nHost: localhost\r\nAuthorization: Nonce {nonce}\r\nAccept: application/json, text/event-stream\r\n{sessionHeader}Content-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: keep-alive\r\n\r\n";
		await pipe.WriteAsync(Encoding.UTF8.GetBytes(request), ct);
		await pipe.WriteAsync(bodyBytes, ct);
		await pipe.FlushAsync(ct);

		var headers = await ReadHeadersAsync(pipe, ct);
		var responseHeaders = ParseHeaders(headers);
		responseHeaders.TryGetValue("mcp-session-id", out var resolvedSessionId);
		var effectiveSessionId = string.IsNullOrWhiteSpace(resolvedSessionId) ? sessionId : resolvedSessionId;

		var responseBody = "";
		if (responseHeaders.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
		{
			responseBody = await ReadChunkedBodyAsync(pipe, ct);
		}
		else if (responseHeaders.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var contentLength) && contentLength > 0)
		{
			var buffer = new byte[contentLength];
			var totalRead = 0;
			while (totalRead < contentLength)
			{
				var read = await pipe.ReadAsync(buffer.AsMemory(totalRead, contentLength - totalRead), ct);
				if (read == 0)
				{
					break;
				}

				totalRead += read;
			}

			responseBody = Encoding.UTF8.GetString(buffer, 0, totalRead);
		}

		return (responseBody, effectiveSessionId);
	}

	private static async Task<string> ReadUntilContainsAsync(Stream stream, string expected, CancellationToken ct)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

		var sb = new StringBuilder();
		var buffer = new byte[1024];
		while (!timeoutCts.IsCancellationRequested)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token);
			if (read == 0)
			{
				break;
			}

			sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
			if (sb.ToString().Contains(expected, StringComparison.Ordinal))
			{
				return sb.ToString();
			}
		}

		throw new Xunit.Sdk.XunitException($"Did not receive expected payload fragment: {expected}\nReceived:\n{sb}");
	}

	private static async Task<string> ReadHeadersAsync(Stream pipe, CancellationToken ct)
	{
		var sb = new StringBuilder();
		var b = new byte[1];
		while (!ct.IsCancellationRequested)
		{
			var read = await pipe.ReadAsync(b.AsMemory(0, 1), ct);
			if (read == 0)
			{
				break;
			}

			sb.Append((char)b[0]);
			if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
			{
				break;
			}
		}

		return sb.ToString();
	}

	private static Dictionary<string, string> ParseHeaders(string rawHeaders)
	{
		var lines = rawHeaders.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		for (var i = 1; i < lines.Length; i++)
		{
			var colonIdx = lines[i].IndexOf(':');
			if (colonIdx <= 0)
			{
				continue;
			}

			headers[lines[i][..colonIdx].Trim()] = lines[i][(colonIdx + 1)..].Trim();
		}

		return headers;
	}

	private static async Task<NamedPipeClientStream> OpenSseGetStreamWithLastEventIdAsync(
		string pipeName, string nonce, string sessionId, string lastEventId, CancellationToken ct)
	{
		var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await pipe.ConnectAsync(ct);

		var request =
			$"GET /mcp HTTP/1.1\r\nHost: localhost\r\nAuthorization: Nonce {nonce}\r\nAccept: text/event-stream\r\nmcp-protocol-version: 2025-11-25\r\nmcp-session-id: {sessionId}\r\nLast-Event-ID: {lastEventId}\r\nConnection: keep-alive\r\n\r\n";
		var bytes = Encoding.UTF8.GetBytes(request);
		await pipe.WriteAsync(bytes, ct);
		await pipe.FlushAsync(ct);

		var headers = await ReadHeadersAsync(pipe, ct);
		Assert.Contains("200", headers);
		Assert.Contains("text/event-stream", headers, StringComparison.OrdinalIgnoreCase);
		return pipe;
	}

	/// <summary>
	/// Extract "id: xxx" values from raw SSE stream data.
	/// Tolerant of chunked framing noise between lines.
	/// </summary>
	private static List<string> ExtractSseEventIds(string rawPayload)
	{
		var ids = new List<string>();
		foreach (var line in rawPayload.Split('\n'))
		{
			var trimmed = line.Trim('\r', ' ');
			if (trimmed.StartsWith("id:", StringComparison.Ordinal))
			{
				var idValue = trimmed[3..].Trim();
				if (!string.IsNullOrWhiteSpace(idValue))
				{
					ids.Add(idValue);
				}
			}
		}

		return ids;
	}

	/// <summary>
	/// Extract the JSON payload from a "data:" line for a given method.
	/// </summary>
	private static string? ExtractSseDataJson(string rawPayload, string method)
	{
		foreach (var line in rawPayload.Split('\n'))
		{
			var trimmed = line.Trim('\r', ' ');
			if (trimmed.StartsWith("data:", StringComparison.Ordinal))
			{
				var json = trimmed[5..].Trim();
				if (json.Contains($"\"method\":\"{method}\"", StringComparison.Ordinal))
				{
					return json;
				}
			}
		}

		return null;
	}

	private static int CountOccurrences(string text, string search)
	{
		var count = 0;
		var idx = 0;
		while ((idx = text.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
		{
			count++;
			idx += search.Length;
		}

		return count;
	}

	private static async Task<string> ReadChunkedBodyAsync(Stream stream, CancellationToken ct)
	{
		var result = new StringBuilder();
		var lineBuf = new StringBuilder();

		while (true)
		{
			lineBuf.Clear();
			while (true)
			{
				var b = new byte[1];
				var read = await stream.ReadAsync(b.AsMemory(0, 1), ct);
				if (read == 0)
				{
					return result.ToString();
				}

				lineBuf.Append((char)b[0]);
				if (lineBuf is [.., '\r', '\n'])
				{
					break;
				}
			}

			var sizeLine = lineBuf.ToString().TrimEnd('\r', '\n').Trim();
			var semiIdx = sizeLine.IndexOf(';');
			if (semiIdx >= 0)
			{
				sizeLine = sizeLine[..semiIdx];
			}

			if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, null, out var chunkSize) || chunkSize == 0)
			{
				break;
			}

			var chunkBuf = new byte[chunkSize];
			var totalRead = 0;
			while (totalRead < chunkSize)
			{
				var read = await stream.ReadAsync(chunkBuf.AsMemory(totalRead, chunkSize - totalRead), ct);
				if (read == 0)
				{
					break;
				}

				totalRead += read;
			}

			result.Append(Encoding.UTF8.GetString(chunkBuf, 0, totalRead));
			await stream.ReadExactlyAsync(new byte[2], ct);
		}

		await stream.ReadExactlyAsync(new byte[2], ct);
		return result.ToString();
	}
}
