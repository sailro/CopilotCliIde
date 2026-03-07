using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CopilotCliIde.Shared;
using NSubstitute;

namespace CopilotCliIde.Server.Tests;

/// <summary>
/// Replay comparison tests that validate our MCP server against real VS Code Insiders
/// traffic captured via the PipeProxy tool. Tests 1-6 are pure capture analysis;
/// test 7 starts our actual MCP server and compares tool names.
/// </summary>
public class TrafficReplayTests : IDisposable
{
	public void Dispose() { }

	/// <summary>
	/// Returns all .ndjson files from the Captures/ directory as test data.
	/// Drop a new capture in Captures/ and it's automatically validated.
	/// </summary>
	public static IEnumerable<object[]> CaptureFiles()
	{
		var capturesDir = FindCapturesDir();
		foreach (var file in Directory.GetFiles(capturesDir, "*.ndjson"))
			yield return [file];
	}

	private static TrafficParser LoadCapture(string path) => TrafficParser.Load(path);

	#region Test 1 — Initialize response structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeInitializeResponse_HasExpectedStructure(string captureFile)
	{
		var _parser = LoadCapture(captureFile);
		var response = _parser.GetInitializeResponse();
		Assert.NotNull(response);

		var result = response.Value.GetProperty("result");

		// protocolVersion
		Assert.True(result.TryGetProperty("protocolVersion", out var pv));
		Assert.Equal(JsonValueKind.String, pv.ValueKind);
		Assert.False(string.IsNullOrEmpty(pv.GetString()));

		// capabilities.tools.listChanged
		var caps = result.GetProperty("capabilities");
		var tools = caps.GetProperty("tools");
		Assert.True(tools.TryGetProperty("listChanged", out var lc));
		Assert.True(lc.ValueKind is JsonValueKind.True or JsonValueKind.False);

		// serverInfo.name
		var serverInfo = result.GetProperty("serverInfo");
		Assert.True(serverInfo.TryGetProperty("name", out var name));
		Assert.Equal(JsonValueKind.String, name.ValueKind);
		Assert.False(string.IsNullOrEmpty(name.GetString()));

		// serverInfo.version
		Assert.True(serverInfo.TryGetProperty("version", out var version));
		Assert.Equal(JsonValueKind.String, version.ValueKind);
		Assert.False(string.IsNullOrEmpty(version.GetString()));
	}

	#endregion

	#region Test 2 — Tools list contains expected tools

	private static readonly HashSet<string> KnownVsCodeTools = new(StringComparer.Ordinal)
	{
		"get_vscode_info",
		"get_selection",
		"open_diff",
		"close_diff",
		"get_diagnostics",
		"update_session_name",
		"read_file",
	};

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeToolsList_ContainsExpectedTools(string captureFile)
	{
		var _parser = LoadCapture(captureFile);
		var response = _parser.GetToolsListResponse();
		Assert.NotNull(response);

		var toolsArray = response.Value.GetProperty("result").GetProperty("tools");
		var toolNames = toolsArray.EnumerateArray()
			.Select(t => t.GetProperty("name").GetString()!)
			.ToHashSet();

		// No unknown tools allowed — every tool must be in the known set
		var unknownTools = toolNames.Where(t => !KnownVsCodeTools.Contains(t)).ToList();
		Assert.True(unknownTools.Count == 0,
			$"Capture registered unknown tools: {string.Join(", ", unknownTools)}. " +
			$"If these are valid tools, add them to KnownVsCodeTools.");
	}

	#endregion

	#region Test 3 — Tool input schemas shape

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeToolsList_ToolInputSchemas_HaveExpectedShape(string captureFile)
	{
		var _parser = LoadCapture(captureFile);
		var response = _parser.GetToolsListResponse();
		Assert.NotNull(response);

		var toolsArray = response.Value.GetProperty("result").GetProperty("tools");

		foreach (var tool in toolsArray.EnumerateArray())
		{
			var toolName = tool.GetProperty("name").GetString()!;

			Assert.True(tool.TryGetProperty("inputSchema", out var schema),
				$"Tool '{toolName}' missing inputSchema");

			// type: "object"
			Assert.True(schema.TryGetProperty("type", out var type),
				$"Tool '{toolName}' inputSchema missing 'type'");
			Assert.Equal("object", type.GetString());

			// properties must exist (even if empty)
			Assert.True(schema.TryGetProperty("properties", out var props),
				$"Tool '{toolName}' inputSchema missing 'properties'");
			Assert.Equal(JsonValueKind.Object, props.ValueKind);
		}
	}

	#endregion

	#region Test 4 — get_diagnostics response structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeGetDiagnosticsResponse_HasExpectedStructure(string captureFile)
	{
		var _parser = LoadCapture(captureFile);
		var response = _parser.GetToolCallResponse("get_diagnostics");
		Assert.NotNull(response);

		var result = response.Value.GetProperty("result");

		// content array
		Assert.True(result.TryGetProperty("content", out var content));
		Assert.Equal(JsonValueKind.Array, content.ValueKind);
		Assert.True(content.GetArrayLength() > 0, "get_diagnostics content should not be empty");

		// First content item has type: "text" and text: (string)
		var firstItem = content[0];
		Assert.Equal("text", firstItem.GetProperty("type").GetString());
		var textValue = firstItem.GetProperty("text").GetString()!;

		// The text field is a JSON array of diagnostics groups
		var diagDoc = JsonDocument.Parse(textValue);
		var diagArray = diagDoc.RootElement;
		Assert.Equal(JsonValueKind.Array, diagArray.ValueKind);

		if (diagArray.GetArrayLength() > 0)
		{
			var firstGroup = diagArray[0];

			// Each group has uri, filePath, diagnostics
			Assert.True(firstGroup.TryGetProperty("uri", out var uri));
			Assert.Equal(JsonValueKind.String, uri.ValueKind);

			Assert.True(firstGroup.TryGetProperty("filePath", out var fp));
			Assert.Equal(JsonValueKind.String, fp.ValueKind);

			Assert.True(firstGroup.TryGetProperty("diagnostics", out var diags));
			Assert.Equal(JsonValueKind.Array, diags.ValueKind);

			if (diags.GetArrayLength() > 0)
			{
				var firstDiag = diags[0];
				Assert.True(firstDiag.TryGetProperty("message", out _));
				Assert.True(firstDiag.TryGetProperty("severity", out _));
				Assert.True(firstDiag.TryGetProperty("range", out var range));
				Assert.True(range.TryGetProperty("start", out var start));
				Assert.True(start.TryGetProperty("line", out _));
				Assert.True(start.TryGetProperty("character", out _));
			}
		}
	}

	#endregion

	#region Test 5 — selection_changed notification structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeSelectionChanged_HasExpectedStructure(string captureFile)
	{
		var _parser = LoadCapture(captureFile);
		var notifications = _parser.GetNotifications("selection_changed");
		Assert.NotEmpty(notifications);

		foreach (var notification in notifications)
		{
			// JSON-RPC envelope
			Assert.Equal("2.0", notification.GetProperty("jsonrpc").GetString());
			Assert.Equal("selection_changed", notification.GetProperty("method").GetString());

			var @params = notification.GetProperty("params");

			// Required fields
			Assert.True(@params.TryGetProperty("text", out var text));
			Assert.Equal(JsonValueKind.String, text.ValueKind);

			Assert.True(@params.TryGetProperty("filePath", out var filePath));
			Assert.Equal(JsonValueKind.String, filePath.ValueKind);

			Assert.True(@params.TryGetProperty("fileUrl", out var fileUrl));
			Assert.Equal(JsonValueKind.String, fileUrl.ValueKind);

			Assert.True(@params.TryGetProperty("selection", out var selection));
			Assert.Equal(JsonValueKind.Object, selection.ValueKind);

			// selection sub-fields
			Assert.True(selection.TryGetProperty("start", out var start));
			Assert.True(start.TryGetProperty("line", out _));
			Assert.True(start.TryGetProperty("character", out _));

			Assert.True(selection.TryGetProperty("end", out var end));
			Assert.True(end.TryGetProperty("line", out _));
			Assert.True(end.TryGetProperty("character", out _));

			Assert.True(selection.TryGetProperty("isEmpty", out var isEmpty));
			Assert.True(isEmpty.ValueKind is JsonValueKind.True or JsonValueKind.False);
		}
	}

	#endregion

	#region Test 6 — diagnostics_changed notification structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeDiagnosticsChanged_HasExpectedStructure(string captureFile)
	{
		var _parser = LoadCapture(captureFile);
		var notifications = _parser.GetNotifications("diagnostics_changed");
		Assert.NotEmpty(notifications);

		foreach (var notification in notifications)
		{
			// JSON-RPC envelope
			Assert.Equal("2.0", notification.GetProperty("jsonrpc").GetString());
			Assert.Equal("diagnostics_changed", notification.GetProperty("method").GetString());

			var @params = notification.GetProperty("params");
			Assert.True(@params.TryGetProperty("uris", out var uris));
			Assert.Equal(JsonValueKind.Array, uris.ValueKind);

			foreach (var entry in uris.EnumerateArray())
			{
				Assert.True(entry.TryGetProperty("uri", out var uri));
				Assert.Equal(JsonValueKind.String, uri.ValueKind);

				Assert.True(entry.TryGetProperty("diagnostics", out var diags));
				Assert.Equal(JsonValueKind.Array, diags.ValueKind);

				// If there are diagnostics, validate their structure
				foreach (var diag in diags.EnumerateArray())
				{
					Assert.True(diag.TryGetProperty("range", out var range));
					Assert.True(range.TryGetProperty("start", out _));
					Assert.True(range.TryGetProperty("end", out _));
					Assert.True(diag.TryGetProperty("message", out _));
					Assert.True(diag.TryGetProperty("severity", out _));
				}
			}
		}
	}

	#endregion

	#region Test 8 — No unknown notification methods in capture

	private static readonly HashSet<string> KnownNotificationMethods = new(StringComparer.Ordinal)
	{
		"selection_changed",
		"diagnostics_changed",
	};

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeCapture_ContainsNoUnknownNotificationMethods(string captureFile)
	{
		var _parser = LoadCapture(captureFile);
		// Extract ALL notification methods from vscode_to_cli entries
		var allMethods = _parser.Entries
			.Where(e => e.Direction == "vscode_to_cli" && e.JsonRpcMessage.HasValue)
			.Select(e =>
			{
				try
				{
					if (e.JsonRpcMessage!.Value.TryGetProperty("method", out var m))
						return m.GetString();
				}
				catch { /* Skip unparseable entries */ }
				return null;
			})
			.Where(m => m != null)
			.Distinct()
			.ToList();

		var unknownMethods = allMethods
			.Where(m => !KnownNotificationMethods.Contains(m!))
			.ToList();

		Assert.True(unknownMethods.Count == 0,
			$"Capture contains unknown notification methods: {string.Join(", ", unknownMethods)}. " +
			$"If these are valid new VS Code notifications, add them to KnownNotificationMethods.");
	}

	#endregion

	#region Test 7 — Our tools/list matches VS Code tool names

	[Fact]
	public async Task OurToolsList_MatchesVsCodeToolNames()
	{
		// Extract VS Code's tool names from the first capture
		var _parser = LoadCapture(FindAllCaptureFiles().First());
		var vsCodeResponse = _parser.GetToolsListResponse();
		Assert.NotNull(vsCodeResponse);
		var vsCodeToolNames = vsCodeResponse.Value.GetProperty("result").GetProperty("tools")
			.EnumerateArray()
			.Select(t => t.GetProperty("name").GetString()!)
			.ToHashSet();

		// Start our MCP server with a mocked IVsServiceRpc
		var mockVsServices = Substitute.For<IVsServiceRpc>();
		var rpcClient = new RpcClient(mockVsServices);

		var pipeName = $"copilot-replay-test-{Guid.NewGuid():N}";
		var nonce = "test-nonce";

		await using var server = new McpPipeServer();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		await server.StartAsync(rpcClient, pipeName, nonce, cts.Token);

		// Connect to the pipe and send initialize + tools/list
		using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await pipe.ConnectAsync(cts.Token);

		// Send initialize
		var initRequest = JsonSerializer.Serialize(new
		{
			method = "initialize",
			@params = new
			{
				protocolVersion = "2025-11-25",
				capabilities = new { },
				clientInfo = new { name = "replay-test", version = "1.0.0" },
			},
			jsonrpc = "2.0",
			id = 0,
		});
		await SendHttpPostAsync(pipe, initRequest, nonce, cts.Token);
		var initResponse = await ReadHttpResponseAsync(pipe, cts.Token);
		Assert.Contains("protocolVersion", initResponse);

		// Send notifications/initialized
		var initializedNotification = JsonSerializer.Serialize(new
		{
			method = "notifications/initialized",
			jsonrpc = "2.0",
		});
		await SendHttpPostAsync(pipe, initializedNotification, nonce, cts.Token);
		await ReadHttpResponseAsync(pipe, cts.Token);

		// Send tools/list
		var toolsListRequest = JsonSerializer.Serialize(new
		{
			method = "tools/list",
			jsonrpc = "2.0",
			id = 1,
		});
		await SendHttpPostAsync(pipe, toolsListRequest, nonce, cts.Token);
		var toolsResponse = await ReadHttpResponseAsync(pipe, cts.Token);

		// Parse the SSE response body — extract the JSON-RPC from the event stream
		var ourToolNames = ExtractToolNamesFromResponse(toolsResponse);

		// Our server should have at least all 6 VS Code tools
		foreach (var vsCodeTool in vsCodeToolNames)
		{
			Assert.Contains(vsCodeTool, ourToolNames);
		}
	}

	#endregion

	#region Helpers

	/// <summary>
	/// Finds the Captures/ directory relative to the test assembly or repo structure.
	/// </summary>
	private static string FindCapturesDir()
	{
		var assemblyDir = Path.GetDirectoryName(typeof(TrafficReplayTests).Assembly.Location)!;

		// First: look next to the assembly (copied via CopyToOutputDirectory)
		var fromAssembly = Path.Combine(assemblyDir, "Captures");
		if (Directory.Exists(fromAssembly))
			return fromAssembly;

		// Fallback: walk up to find the test project source directory
		var dir = assemblyDir;
		while (dir != null)
		{
			var candidate = Path.Combine(dir, "src", "CopilotCliIde.Server.Tests", "Captures");
			if (Directory.Exists(candidate))
				return candidate;
			candidate = Path.Combine(dir, "Captures");
			if (Directory.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException(
			"Captures directory not found. Expected at src/CopilotCliIde.Server.Tests/Captures/");
	}

	private static IEnumerable<string> FindAllCaptureFiles() =>
		Directory.GetFiles(FindCapturesDir(), "*.ndjson");

	private static async Task SendHttpPostAsync(Stream pipe, string body, string nonce, CancellationToken ct)
	{
		var bodyBytes = Encoding.UTF8.GetBytes(body);
		var request = $"POST /mcp HTTP/1.1\r\nAuthorization: Nonce {nonce}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: keep-alive\r\n\r\n";
		var headerBytes = Encoding.UTF8.GetBytes(request);

		await pipe.WriteAsync(headerBytes, ct);
		await pipe.WriteAsync(bodyBytes, ct);
		await pipe.FlushAsync(ct);
	}

	private static async Task<string> ReadHttpResponseAsync(Stream pipe, CancellationToken ct)
	{
		// Read HTTP response headers byte-by-byte until \r\n\r\n
		var sb = new StringBuilder();
		var buffer = new byte[1];
		while (!ct.IsCancellationRequested)
		{
			var read = await pipe.ReadAsync(buffer.AsMemory(0, 1), ct);
			if (read == 0) break;
			sb.Append((char)buffer[0]);
			if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
				break;
		}

		var headerText = sb.ToString();
		var lines = headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		for (var i = 1; i < lines.Length; i++)
		{
			var colonIdx = lines[i].IndexOf(':');
			if (colonIdx > 0)
			{
				var key = lines[i][..colonIdx].Trim();
				var value = lines[i][(colonIdx + 1)..].Trim();
				headers[key] = value;
			}
		}

		if (headers.TryGetValue("content-length", out var clStr) && int.TryParse(clStr, out var contentLength) && contentLength > 0)
		{
			var bodyBuffer = new byte[contentLength];
			var totalRead = 0;
			while (totalRead < contentLength)
			{
				var read = await pipe.ReadAsync(bodyBuffer.AsMemory(totalRead, contentLength - totalRead), ct);
				if (read == 0) break;
				totalRead += read;
			}
			return Encoding.UTF8.GetString(bodyBuffer, 0, totalRead);
		}

		return "";
	}

	private static HashSet<string> ExtractToolNamesFromResponse(string responseBody)
	{
		var names = new HashSet<string>();

		// The response is SSE format: "event: message\ndata: {json}\n\n"
		// Try parsing as SSE first
		foreach (var line in responseBody.Split('\n'))
		{
			var trimmed = line.Trim();
			if (trimmed.StartsWith("data:", StringComparison.Ordinal))
			{
				var json = trimmed["data:".Length..].Trim();
				if (TryExtractToolNames(json, names))
					return names;
			}
		}

		// Fallback: try parsing the whole thing as JSON-RPC
		if (TryExtractToolNames(responseBody, names))
			return names;

		// Final fallback: find JSON object in the response
		var jsonStart = responseBody.IndexOf('{');
		if (jsonStart >= 0)
		{
			var jsonEnd = responseBody.LastIndexOf('}');
			if (jsonEnd > jsonStart)
			{
				var json = responseBody[jsonStart..(jsonEnd + 1)];
				TryExtractToolNames(json, names);
			}
		}

		return names;
	}

	private static bool TryExtractToolNames(string json, HashSet<string> names)
	{
		try
		{
			var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("result", out var result) &&
				result.TryGetProperty("tools", out var tools))
			{
				foreach (var tool in tools.EnumerateArray())
				{
					if (tool.TryGetProperty("name", out var name))
						names.Add(name.GetString()!);
				}
				return names.Count > 0;
			}
		}
		catch { /* Not valid JSON-RPC */ }
		return false;
	}

	#endregion
}
