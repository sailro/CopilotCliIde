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
public class TrafficReplayTests
{

	/// <summary>
	/// Returns all .ndjson files from the Captures/ directory as test data.
	/// Drop a new capture in Captures/ and it's automatically validated.
	/// </summary>
	public static TheoryData<string> CaptureFiles()
	{
		var data = new TheoryData<string>();
		foreach (var file in GetCaptureFiles())
			data.Add(file);
		return data;
	}

	private static TrafficParser LoadCapture(string path) => TrafficParser.Load(path);

	#region Test 1 — Initialize response structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeInitializeResponse_HasExpectedStructure(string captureFile)
	{
		var parser = LoadCapture(captureFile);
		var response = parser.GetInitializeResponse();
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

	private static readonly HashSet<string> _knownVsCodeTools =
	[
		"get_vscode_info",
		"get_selection",
		"open_diff",
		"close_diff",
		"get_diagnostics",
		"update_session_name",
		"read_file"
	];

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeToolsList_ContainsExpectedTools(string captureFile)
	{
		var parser = LoadCapture(captureFile);
		var response = parser.GetToolsListResponse();
		Assert.NotNull(response);

		var toolsArray = response.Value.GetProperty("result").GetProperty("tools");
		var toolNames = toolsArray.EnumerateArray()
			.Select(t => t.GetProperty("name").GetString()!)
			.ToHashSet();

		// No unknown tools allowed — every tool must be in the known set
		var unknownTools = toolNames.Where(t => !_knownVsCodeTools.Contains(t)).ToList();
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
		var parser = LoadCapture(captureFile);
		var response = parser.GetToolsListResponse();
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
		var parser = LoadCapture(captureFile);
		var response = parser.GetToolCallResponse("get_diagnostics");
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

		if (diagArray.GetArrayLength() == 0)
			return;

		var firstGroup = diagArray[0];

		// Each group has uri, filePath, diagnostics
		Assert.True(firstGroup.TryGetProperty("uri", out var uri));
		Assert.Equal(JsonValueKind.String, uri.ValueKind);

		Assert.True(firstGroup.TryGetProperty("filePath", out var fp));
		Assert.Equal(JsonValueKind.String, fp.ValueKind);

		Assert.True(firstGroup.TryGetProperty("diagnostics", out var diags));
		Assert.Equal(JsonValueKind.Array, diags.ValueKind);

		if (diags.GetArrayLength() == 0)
			return;

		var firstDiag = diags[0];
		Assert.True(firstDiag.TryGetProperty("message", out _));
		Assert.True(firstDiag.TryGetProperty("severity", out _));
		Assert.True(firstDiag.TryGetProperty("range", out var range));
		Assert.True(range.TryGetProperty("start", out var start));
		Assert.True(start.TryGetProperty("line", out _));
		Assert.True(start.TryGetProperty("character", out _));
	}

	#endregion

	#region Test 5 — selection_changed notification structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeSelectionChanged_HasExpectedStructure(string captureFile)
	{
		var parser = LoadCapture(captureFile);
		var notifications = parser.GetNotifications("selection_changed");
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
		var parser = LoadCapture(captureFile);
		var notifications = parser.GetNotifications("diagnostics_changed");
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

					// source and code are present on the wire in some captures
					// (vs-1.0.7 has source; VS Code 0.38 may not).
					// When present, validate their types.
					if (diag.TryGetProperty("source", out var source))
					{
						Assert.True(
							source.ValueKind is JsonValueKind.String or JsonValueKind.Null,
							$"diagnostic 'source' should be string or null, got {source.ValueKind}");
					}
					if (diag.TryGetProperty("code", out var code))
					{
						Assert.True(
							code.ValueKind is JsonValueKind.String or JsonValueKind.Null,
							$"diagnostic 'code' should be string or null, got {code.ValueKind}");
					}
				}
			}
		}
	}

	#endregion

	#region Test 8 — No unknown notification methods in capture

	private static readonly HashSet<string> _knownNotificationMethods =
	[
		"selection_changed",
		"diagnostics_changed"
	];

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeCapture_ContainsNoUnknownNotificationMethods(string captureFile)
	{
		var parser = LoadCapture(captureFile);
		// Extract ALL notification methods from vscode_to_cli entries
		var allMethods = parser.Entries
			.Where(e => e is { Direction: "vscode_to_cli", JsonRpcMessage: not null })
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
			.Where(m => !_knownNotificationMethods.Contains(m!))
			.ToList();

		Assert.True(unknownMethods.Count == 0,
			$"Capture contains unknown notification methods: {string.Join(", ", unknownMethods)}. " +
			$"If these are valid new VS Code notifications, add them to KnownNotificationMethods.");
	}

	#endregion

	#region Test A1 — Cross-capture tool input schema consistency

	/// <summary>
	/// Compares each tool's inputSchema (property names, types, required) across ALL capture files.
	/// Flags any unexpected differences.
	/// </summary>
	[Fact]
	public void AllCaptures_ToolInputSchemas_AreConsistent()
	{
		var captureFiles = GetCaptureFiles();
		Assert.True(captureFiles.Length >= 2, "Need at least 2 captures for cross-capture comparison");

		// Build per-tool, per-capture schema data:
		// toolName -> list of (captureFile, properties dict, required set)
		var toolSchemas = new Dictionary<string, List<(string Capture, Dictionary<string, string> Props, HashSet<string> Required)>>();

		foreach (var captureFile in captureFiles)
		{
			var parser = LoadCapture(captureFile);
			var response = parser.GetToolsListResponse();
			if (response is null)
				continue;

			var captureName = Path.GetFileName(captureFile);
			var toolsArray = response.Value.GetProperty("result").GetProperty("tools");

			foreach (var tool in toolsArray.EnumerateArray())
			{
				var toolName = tool.GetProperty("name").GetString()!;
				var schema = tool.GetProperty("inputSchema");
				var properties = schema.GetProperty("properties");

				// Extract property names and their types
				var propDict = new Dictionary<string, string>();
				foreach (var prop in properties.EnumerateObject())
				{
					var propType = prop.Value.TryGetProperty("type", out var typeEl)
						? typeEl.ToString()
						: "unknown";
					propDict[prop.Name] = propType;
				}

				// Extract required fields
				var requiredSet = new HashSet<string>();
				if (schema.TryGetProperty("required", out var requiredArray))
				{
					foreach (var req in requiredArray.EnumerateArray())
						requiredSet.Add(req.GetString()!);
				}

				if (!toolSchemas.ContainsKey(toolName))
					toolSchemas[toolName] = [];
				toolSchemas[toolName].Add((captureName, propDict, requiredSet));
			}
		}

		// Compare schemas across captures for each tool
		var differences = new List<string>();

		foreach (var (toolName, schemas) in toolSchemas)
		{
			if (schemas.Count < 2)
				continue; // Tool only appears in one capture — nothing to compare

			var (baselineCapture, baselineProps, baselineRequired) = schemas[0];

			for (var i = 1; i < schemas.Count; i++)
			{
				var (otherCapture, otherProps, otherRequired) = schemas[i];

				// Compare property names
				var baselinePropNames = baselineProps.Keys.ToHashSet();
				var otherPropNames = otherProps.Keys.ToHashSet();

				var missingInOther = baselinePropNames.Except(otherPropNames).ToList();
				var extraInOther = otherPropNames.Except(baselinePropNames).ToList();

				foreach (var missing in missingInOther)
					differences.Add($"{toolName}: property '{missing}' in {baselineCapture} but missing in {otherCapture}");
				foreach (var extra in extraInOther)
					differences.Add($"{toolName}: property '{extra}' in {otherCapture} but missing in {baselineCapture}");

				// Compare property types for shared properties
				differences.AddRange(
					from prop in baselinePropNames.Intersect(otherPropNames)
					where baselineProps[prop] != otherProps[prop]
					select $"{toolName}.{prop}: type '{baselineProps[prop]}' in {baselineCapture} " +
						$"vs '{otherProps[prop]}' in {otherCapture}");

				// Compare required fields
				var missingRequired = baselineRequired.Except(otherRequired).ToList();
				var extraRequired = otherRequired.Except(baselineRequired).ToList();

				foreach (var missing in missingRequired)
					differences.Add($"{toolName}: required '{missing}' in {baselineCapture} but not in {otherCapture}");
				foreach (var extra in extraRequired)
					differences.Add($"{toolName}: required '{extra}' in {otherCapture} but not in {baselineCapture}");
			}
		}

		Assert.True(differences.Count == 0,
			"Schema inconsistencies found across captures:\n" +
			string.Join("\n", differences));
	}

	#endregion

	#region Test B1 — get_selection response structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeGetSelectionResponse_HasExpectedStructure(string captureFile)
	{
		var parser = LoadCapture(captureFile);
		var response = parser.GetToolCallResponse("get_selection");
		Assert.NotNull(response);

		var result = response.Value.GetProperty("result");

		// content array with at least one item
		Assert.True(result.TryGetProperty("content", out var content));
		Assert.Equal(JsonValueKind.Array, content.ValueKind);
		Assert.True(content.GetArrayLength() > 0, "get_selection content should not be empty");

		// First content item has type: "text"
		var firstItem = content[0];
		Assert.Equal("text", firstItem.GetProperty("type").GetString());

		var textValue = firstItem.GetProperty("text").GetString()!;

		// text can be "null" (string literal) when no editor is active
		if (textValue == "null")
			return; // Valid response — no active editor

		// Otherwise it should be a JSON object with selection data
		var selDoc = JsonDocument.Parse(textValue);
		var sel = selDoc.RootElement;
		Assert.Equal(JsonValueKind.Object, sel.ValueKind);

		// Required fields
		Assert.True(sel.TryGetProperty("current", out var current),
			"get_selection response missing 'current'");
		Assert.True(current.ValueKind is JsonValueKind.True or JsonValueKind.False);

		// When current is false, filePath/fileUrl/selection may be absent
		// (vs-1.0.7 returns {text: "", current: false} when no editor is active)
		if (current.ValueKind == JsonValueKind.False)
			return;

		Assert.True(sel.TryGetProperty("filePath", out var filePath),
			"get_selection response missing 'filePath'");
		Assert.Equal(JsonValueKind.String, filePath.ValueKind);

		Assert.True(sel.TryGetProperty("fileUrl", out var fileUrl),
			"get_selection response missing 'fileUrl'");
		Assert.Equal(JsonValueKind.String, fileUrl.ValueKind);

		Assert.True(sel.TryGetProperty("selection", out var selection),
			"get_selection response missing 'selection'");
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

		Assert.True(sel.TryGetProperty("text", out var selText),
			"get_selection response missing 'text'");
		Assert.Equal(JsonValueKind.String, selText.ValueKind);
	}

	#endregion

	#region Test B2 — update_session_name response structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void VsCodeUpdateSessionNameResponse_HasExpectedStructure(string captureFile)
	{
		var parser = LoadCapture(captureFile);
		var response = parser.GetToolCallResponse("update_session_name");
		if (response is null)
			return; // Tool not called in this capture — skip validation

		var result = response.Value.GetProperty("result");

		// content array
		Assert.True(result.TryGetProperty("content", out var content));
		Assert.Equal(JsonValueKind.Array, content.ValueKind);
		Assert.True(content.GetArrayLength() > 0, "update_session_name content should not be empty");

		// First content item has type: "text"
		var firstItem = content[0];
		Assert.Equal("text", firstItem.GetProperty("type").GetString());

		var textValue = firstItem.GetProperty("text").GetString()!;

		// text should be a JSON object containing {"success": true}
		var doc = JsonDocument.Parse(textValue);
		var root = doc.RootElement;
		Assert.Equal(JsonValueKind.Object, root.ValueKind);

		Assert.True(root.TryGetProperty("success", out var success),
			"update_session_name response missing 'success'");
		Assert.Equal(JsonValueKind.True, success.ValueKind);
	}

	#endregion

	#region Test E1 — Our server initialize response structure

	[Fact]
	public async Task OurServer_InitializeResponse_HasExpectedStructure()
	{
		// Start our MCP server with a mocked IVsServiceRpc
		var mockVsServices = Substitute.For<IVsServiceRpc>();
		var rpcClient = new RpcClient(mockVsServices);

		var pipeName = $"copilot-replay-test-{Guid.NewGuid():N}";
		const string nonce = "test-nonce";

		await using var server = new McpPipeServer();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		await server.StartAsync(rpcClient, pipeName, nonce, cts.Token);

		// Connect to the pipe
		await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await pipe.ConnectAsync(cts.Token);

		// Send initialize
		var initRequest = JsonSerializer.Serialize(new
		{
			method = "initialize",
			@params = new
			{
				protocolVersion = "2025-11-25",
				capabilities = new { },
				clientInfo = new { name = "replay-test", version = "1.0.0" }
			},
			jsonrpc = "2.0",
			id = 0
		});
		await SendHttpPostAsync(pipe, initRequest, nonce, cts.Token);
		var initResponseBody = await ReadHttpResponseAsync(pipe, cts.Token);

		// Parse the response — may be SSE format or direct JSON
		var initJson = ExtractJsonRpcFromResponse(initResponseBody);
		Assert.NotNull(initJson);

		var result = initJson.Value.GetProperty("result");

		// protocolVersion
		Assert.True(result.TryGetProperty("protocolVersion", out var pv),
			"Our initialize response missing 'protocolVersion'");
		Assert.Equal(JsonValueKind.String, pv.ValueKind);
		Assert.False(string.IsNullOrEmpty(pv.GetString()));

		// capabilities.tools.listChanged
		Assert.True(result.TryGetProperty("capabilities", out var caps),
			"Our initialize response missing 'capabilities'");
		var tools = caps.GetProperty("tools");
		Assert.True(tools.TryGetProperty("listChanged", out var lc),
			"Our initialize response missing 'capabilities.tools.listChanged'");
		Assert.True(lc.ValueKind is JsonValueKind.True or JsonValueKind.False);

		// serverInfo.name
		Assert.True(result.TryGetProperty("serverInfo", out var serverInfo),
			"Our initialize response missing 'serverInfo'");
		Assert.True(serverInfo.TryGetProperty("name", out var name),
			"Our initialize response missing 'serverInfo.name'");
		Assert.Equal(JsonValueKind.String, name.ValueKind);
		Assert.False(string.IsNullOrEmpty(name.GetString()));

		// serverInfo.version
		Assert.True(serverInfo.TryGetProperty("version", out var version),
			"Our initialize response missing 'serverInfo.version'");
		Assert.Equal(JsonValueKind.String, version.ValueKind);
		Assert.False(string.IsNullOrEmpty(version.GetString()));
	}

	#endregion

	#region Test E2 — Our server get_selection response structure

	[Fact]
	public async Task OurServer_GetSelectionResponse_HasExpectedStructure()
	{
		// Mock IVsServiceRpc to return a realistic SelectionResult
		var mockVsServices = Substitute.For<IVsServiceRpc>();
		mockVsServices.GetSelectionAsync().Returns(Task.FromResult(new SelectionResult
		{
			Current = true,
			FilePath = @"C:\Projects\Example\Program.cs",
			FileUrl = "file:///C:/Projects/Example/Program.cs",
			Text = "Console.WriteLine(\"Hello\");",
			Selection = new SelectionRange
			{
				Start = new SelectionPosition { Line = 10, Character = 8 },
				End = new SelectionPosition { Line = 10, Character = 36 },
				IsEmpty = false
			}
		}));

		var rpcClient = new RpcClient(mockVsServices);

		var pipeName = $"copilot-replay-test-{Guid.NewGuid():N}";
		const string nonce = "test-nonce";

		await using var server = new McpPipeServer();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		await server.StartAsync(rpcClient, pipeName, nonce, cts.Token);

		// Connect and initialize
		await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await pipe.ConnectAsync(cts.Token);

		var initRequest = JsonSerializer.Serialize(new
		{
			method = "initialize",
			@params = new
			{
				protocolVersion = "2025-11-25",
				capabilities = new { },
				clientInfo = new { name = "replay-test", version = "1.0.0" }
			},
			jsonrpc = "2.0",
			id = 0
		});
		await SendHttpPostAsync(pipe, initRequest, nonce, cts.Token);
		await ReadHttpResponseAsync(pipe, cts.Token);

		// Send notifications/initialized
		var initializedNotification = JsonSerializer.Serialize(new
		{
			method = "notifications/initialized",
			jsonrpc = "2.0"
		});
		await SendHttpPostAsync(pipe, initializedNotification, nonce, cts.Token);
		await ReadHttpResponseAsync(pipe, cts.Token);

		// Call tools/call with get_selection
		var getSelectionRequest = JsonSerializer.Serialize(new
		{
			method = "tools/call",
			@params = new
			{
				name = "get_selection",
				arguments = new { }
			},
			jsonrpc = "2.0",
			id = 2
		});
		await SendHttpPostAsync(pipe, getSelectionRequest, nonce, cts.Token);
		var responseBody = await ReadHttpResponseAsync(pipe, cts.Token);

		var json = ExtractJsonRpcFromResponse(responseBody);
		Assert.NotNull(json);

		var result = json.Value.GetProperty("result");
		var content = result.GetProperty("content");
		Assert.Equal(JsonValueKind.Array, content.ValueKind);
		Assert.True(content.GetArrayLength() > 0);

		var firstItem = content[0];
		Assert.Equal("text", firstItem.GetProperty("type").GetString());

		var textValue = firstItem.GetProperty("text").GetString()!;
		var sel = JsonDocument.Parse(textValue).RootElement;
		Assert.Equal(JsonValueKind.Object, sel.ValueKind);

		// Validate all expected fields
		Assert.True(sel.TryGetProperty("text", out var text),
			"get_selection response missing 'text'");
		Assert.Equal(JsonValueKind.String, text.ValueKind);
		Assert.Equal("Console.WriteLine(\"Hello\");", text.GetString());

		Assert.True(sel.TryGetProperty("filePath", out var filePath),
			"get_selection response missing 'filePath'");
		Assert.Equal(JsonValueKind.String, filePath.ValueKind);

		Assert.True(sel.TryGetProperty("fileUrl", out var fileUrl),
			"get_selection response missing 'fileUrl'");
		Assert.Equal(JsonValueKind.String, fileUrl.ValueKind);

		Assert.True(sel.TryGetProperty("current", out var current),
			"get_selection response missing 'current'");
		Assert.Equal(JsonValueKind.True, current.ValueKind);

		Assert.True(sel.TryGetProperty("selection", out var selection),
			"get_selection response missing 'selection'");
		Assert.Equal(JsonValueKind.Object, selection.ValueKind);

		// Validate selection sub-fields
		var start = selection.GetProperty("start");
		Assert.Equal(10, start.GetProperty("line").GetInt32());
		Assert.Equal(8, start.GetProperty("character").GetInt32());

		var end = selection.GetProperty("end");
		Assert.Equal(10, end.GetProperty("line").GetInt32());
		Assert.Equal(36, end.GetProperty("character").GetInt32());

		Assert.True(selection.TryGetProperty("isEmpty", out var isEmpty));
		Assert.Equal(JsonValueKind.False, isEmpty.ValueKind);
	}

	#endregion

	#region Test E3 — Auth rejection (wrong nonce)

	[Fact]
	public async Task OurServer_InvalidNonce_Returns401()
	{
		// Start server with correct nonce
		var mockVsServices = Substitute.For<IVsServiceRpc>();
		var rpcClient = new RpcClient(mockVsServices);

		var pipeName = $"copilot-replay-test-{Guid.NewGuid():N}";
		const string correctNonce = "correct-nonce";
		const string wrongNonce = "wrong-nonce";

		await using var server = new McpPipeServer();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		await server.StartAsync(rpcClient, pipeName, correctNonce, cts.Token);

		// Connect to the pipe
		await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await pipe.ConnectAsync(cts.Token);

		// Send a request with the WRONG nonce
		var initRequest = JsonSerializer.Serialize(new
		{
			method = "initialize",
			@params = new
			{
				protocolVersion = "2025-11-25",
				capabilities = new { },
				clientInfo = new { name = "replay-test", version = "1.0.0" }
			},
			jsonrpc = "2.0",
			id = 0
		});
		await SendHttpPostAsync(pipe, initRequest, wrongNonce, cts.Token);

		// Read the raw HTTP response — should be 401
		var sb = new StringBuilder();
		var buffer = new byte[1];
		while (!cts.IsCancellationRequested)
		{
			var read = await pipe.ReadAsync(buffer.AsMemory(0, 1), cts.Token);
			if (read == 0) break;
			sb.Append((char)buffer[0]);
			if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
				break;
		}

		var rawResponse = sb.ToString();
		Assert.Contains("401", rawResponse);
		Assert.Contains("Unauthorized", rawResponse);
	}

	#endregion

	#region Test C1 — Request-response ID correlation

	/// <summary>
	/// Verifies request-response ID correlation within sequence order.
	/// For every response with a parseable ID, there should be a request with the same ID
	/// that precedes it in sequence order. IDs may repeat across sessions — that's valid.
	/// </summary>
	[Fact]
	public void AllCaptures_RequestResponseIds_AreCorrelated()
	{
		var captureFiles = GetCaptureFiles();
		var allErrors = new List<string>();

		foreach (var captureFile in captureFiles)
		{
			var captureName = Path.GetFileName(captureFile);
			var parser = LoadCapture(captureFile);

			// Collect all (seq, id) pairs for requests and responses
			var requests = new List<(int Seq, long Id)>();
			var responses = new List<(int Seq, long Id)>();

			foreach (var entry in parser.Entries)
			{
				if (entry.JsonRpcMessage is null)
					continue;

				var msg = entry.JsonRpcMessage.Value;

				switch (entry.Direction)
				{
					case "cli_to_vscode":
						// Notifications don't have id fields — skip them
						if (msg.TryGetProperty("id", out var reqIdEl)
							&& reqIdEl.ValueKind == JsonValueKind.Number
							&& reqIdEl.TryGetInt64(out var reqIdNum))
						{
							requests.Add((entry.Seq, reqIdNum));
						}
						break;
					case "vscode_to_cli":
						// Notifications (method-bearing messages without id) are not responses
						if (msg.TryGetProperty("method", out _) && !msg.TryGetProperty("id", out _))
							continue;

						if (msg.TryGetProperty("id", out var respIdEl)
							&& respIdEl.ValueKind == JsonValueKind.Number
							&& respIdEl.TryGetInt64(out var respIdNum))
						{
							responses.Add((entry.Seq, respIdNum));
						}
						break;
				}
			}

			var hasTruncatedRequests = parser.Entries.Any(e =>
				e is { Direction: "cli_to_vscode", Event: not null, JsonRpcMessage: null });

			// For each response, verify there's a request with the same ID before it
			foreach (var (respSeq, respId) in responses)
			{
				var hasMatchingRequest = requests.Any(r => r.Id == respId && r.Seq < respSeq);
				if (!hasMatchingRequest && !hasTruncatedRequests)
				{
					allErrors.Add(
						$"{captureName}: Response id={respId} at seq={respSeq} has no preceding request");
				}
			}

			// For each request, verify there's a response with the same ID after it
			foreach (var (reqSeq, reqId) in requests)
			{
				var hasMatchingResponse = responses.Any(r => r.Id == reqId && r.Seq > reqSeq);
				if (!hasMatchingResponse)
				{
					allErrors.Add(
						$"{captureName}: Request id={reqId} at seq={reqSeq} has no subsequent response");
				}
			}
		}

		Assert.True(allErrors.Count == 0,
			$"Request-response ID correlation errors:\n{string.Join("\n", allErrors)}");
	}

	#endregion

	#region Test 7 — Our tools/list matches VS Code tool names

	[Fact]
	public async Task OurToolsList_MatchesVsCodeToolNames()
	{
		// Extract VS Code's tool names from the first capture
		var firstCapture = GetCaptureFiles().First();
		var parser = LoadCapture(firstCapture);
		var vsCodeResponse = parser.GetToolsListResponse();
		Assert.NotNull(vsCodeResponse);
		var vsCodeToolNames = vsCodeResponse.Value.GetProperty("result").GetProperty("tools")
			.EnumerateArray()
			.Select(t => t.GetProperty("name").GetString()!)
			.ToHashSet();

		// Start our MCP server with a mocked IVsServiceRpc
		var mockVsServices = Substitute.For<IVsServiceRpc>();
		var rpcClient = new RpcClient(mockVsServices);

		var pipeName = $"copilot-replay-test-{Guid.NewGuid():N}";
		const string nonce = "test-nonce";

		await using var server = new McpPipeServer();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		await server.StartAsync(rpcClient, pipeName, nonce, cts.Token);

		// Connect to the pipe and send initialize + tools/list
		await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await pipe.ConnectAsync(cts.Token);

		// Send initialize
		var initRequest = JsonSerializer.Serialize(new
		{
			method = "initialize",
			@params = new
			{
				protocolVersion = "2025-11-25",
				capabilities = new { },
				clientInfo = new { name = "replay-test", version = "1.0.0" }
			},
			jsonrpc = "2.0",
			id = 0
		});
		await SendHttpPostAsync(pipe, initRequest, nonce, cts.Token);
		var initResponse = await ReadHttpResponseAsync(pipe, cts.Token);
		Assert.Contains("protocolVersion", initResponse);

		// Send notifications/initialized
		var initializedNotification = JsonSerializer.Serialize(new
		{
			method = "notifications/initialized",
			jsonrpc = "2.0"
		});
		await SendHttpPostAsync(pipe, initializedNotification, nonce, cts.Token);
		await ReadHttpResponseAsync(pipe, cts.Token);

		// Send tools/list
		var toolsListRequest = JsonSerializer.Serialize(new
		{
			method = "tools/list",
			jsonrpc = "2.0",
			id = 1
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

	#region Test B3 — open_diff response structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void OpenDiffResponse_HasExpectedStructure(string captureFile)
	{
		var parser = LoadCapture(captureFile);
		var responses = parser.GetAllToolCallResponses("open_diff");
		if (responses.Count == 0)
			return; // No open_diff calls in this capture — skip

		var knownTriggers = new HashSet<string>
		{
			DiffTrigger.AcceptedViaButton,
			DiffTrigger.RejectedViaButton,
			DiffTrigger.ClosedViaTool,
			DiffTrigger.ClosedViaTab,
			DiffTrigger.Timeout
		};

		foreach (var response in responses)
		{
			var result = response.GetProperty("result");

			// MCP envelope: content array with type: "text"
			Assert.True(result.TryGetProperty("content", out var content));
			Assert.Equal(JsonValueKind.Array, content.ValueKind);
			Assert.True(content.GetArrayLength() > 0, "open_diff content should not be empty");
			var firstItem = content[0];
			Assert.Equal("text", firstItem.GetProperty("type").GetString());

			// Parse the inner JSON
			var textValue = firstItem.GetProperty("text").GetString()!;
			var doc = JsonDocument.Parse(textValue);
			var root = doc.RootElement;

			// Required fields
			Assert.True(root.TryGetProperty("success", out var success),
				"open_diff response missing 'success'");
			Assert.True(success.ValueKind is JsonValueKind.True or JsonValueKind.False);

			Assert.True(root.TryGetProperty("tab_name", out var tabName),
				"open_diff response missing 'tab_name'");
			Assert.Equal(JsonValueKind.String, tabName.ValueKind);

			Assert.True(root.TryGetProperty("message", out var message),
				"open_diff response missing 'message'");
			Assert.Equal(JsonValueKind.String, message.ValueKind);

			// When success == true, validate result and trigger
			if (!success.GetBoolean())
				continue;

			Assert.True(root.TryGetProperty("result", out var diffResult),
				"open_diff success response missing 'result'");
			var resultStr = diffResult.GetString();
			Assert.True(resultStr is DiffOutcome.Saved or DiffOutcome.Rejected,
				$"open_diff result should be SAVED or REJECTED, got '{resultStr}'");

			Assert.True(root.TryGetProperty("trigger", out var trigger),
				"open_diff success response missing 'trigger'");
			var triggerStr = trigger.GetString();
			Assert.Contains(triggerStr, (ISet<string?>)knownTriggers);
		}
	}

	#endregion

	#region Test B4 — close_diff response structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void CloseDiffResponse_HasExpectedStructure(string captureFile)
	{
		var parser = LoadCapture(captureFile);
		var responses = parser.GetAllToolCallResponses("close_diff");
		if (responses.Count == 0)
			return; // No close_diff calls in this capture — skip

		foreach (var response in responses)
		{
			var result = response.GetProperty("result");

			// MCP envelope: content array with type: "text"
			Assert.True(result.TryGetProperty("content", out var content));
			Assert.Equal(JsonValueKind.Array, content.ValueKind);
			Assert.True(content.GetArrayLength() > 0, "close_diff content should not be empty");
			var firstItem = content[0];
			Assert.Equal("text", firstItem.GetProperty("type").GetString());

			// Parse the inner JSON
			var textValue = firstItem.GetProperty("text").GetString()!;
			var doc = JsonDocument.Parse(textValue);
			var root = doc.RootElement;

			// Required fields
			Assert.True(root.TryGetProperty("success", out var success),
				"close_diff response missing 'success'");
			Assert.True(success.ValueKind is JsonValueKind.True or JsonValueKind.False);

			Assert.True(root.TryGetProperty("already_closed", out var alreadyClosed),
				"close_diff response missing 'already_closed'");
			Assert.True(alreadyClosed.ValueKind is JsonValueKind.True or JsonValueKind.False);

			Assert.True(root.TryGetProperty("tab_name", out var tabName),
				"close_diff response missing 'tab_name'");
			Assert.Equal(JsonValueKind.String, tabName.ValueKind);

			Assert.True(root.TryGetProperty("message", out var message),
				"close_diff response missing 'message'");
			Assert.Equal(JsonValueKind.String, message.ValueKind);
		}
	}

	#endregion

	#region Test B5 — get_vscode_info response structure

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void GetVsCodeInfoResponse_HasExpectedStructure(string captureFile)
	{
		var parser = LoadCapture(captureFile);
		var responses = parser.GetAllToolCallResponses("get_vscode_info");
		if (responses.Count == 0)
			return; // No get_vscode_info calls in this capture — skip

		foreach (var response in responses)
		{
			var result = response.GetProperty("result");

			// MCP envelope: content array with type: "text"
			Assert.True(result.TryGetProperty("content", out var content));
			Assert.Equal(JsonValueKind.Array, content.ValueKind);
			Assert.True(content.GetArrayLength() > 0, "get_vscode_info content should not be empty");
			var firstItem = content[0];
			Assert.Equal("text", firstItem.GetProperty("type").GetString());

			// Parse the inner JSON
			var textValue = firstItem.GetProperty("text").GetString()!;
			var doc = JsonDocument.Parse(textValue);
			var root = doc.RootElement;
			Assert.Equal(JsonValueKind.Object, root.ValueKind);

			// Common fields present in ALL captures (VS Code and VS)
			Assert.True(root.TryGetProperty("appName", out var appName),
				"get_vscode_info response missing 'appName'");
			Assert.Equal(JsonValueKind.String, appName.ValueKind);
			Assert.False(string.IsNullOrEmpty(appName.GetString()));

			Assert.True(root.TryGetProperty("version", out var version),
				"get_vscode_info response missing 'version'");
			Assert.Equal(JsonValueKind.String, version.ValueKind);
			Assert.False(string.IsNullOrEmpty(version.GetString()));
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

	/// <summary>
	/// Returns all .ndjson capture files from the Captures/ directory.
	/// </summary>
	private static string[] GetCaptureFiles()
	{
		return Directory.GetFiles(FindCapturesDir(), "*.ndjson");
	}

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
			if (colonIdx <= 0) continue;
			var key = lines[i][..colonIdx].Trim();
			var value = lines[i][(colonIdx + 1)..].Trim();
			headers[key] = value;
		}

		if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
			return await McpPipeServer.ReadChunkedBodyAsync(pipe, ct);

		if (!headers.TryGetValue("content-length", out var clStr) || !int.TryParse(clStr, out var contentLength) || contentLength <= 0)
			return "";

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

	private static HashSet<string> ExtractToolNamesFromResponse(string responseBody)
	{
		var names = new HashSet<string>();

		// The response is SSE format: "event: message\ndata: {json}\n\n"
		// Try parsing as SSE first
		foreach (var line in responseBody.Split('\n'))
		{
			var trimmed = line.Trim();
			if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
			var json = trimmed["data:".Length..].Trim();
			if (TryExtractToolNames(json, names))
				return names;
		}

		// Fallback: try parsing the whole thing as JSON-RPC
		if (TryExtractToolNames(responseBody, names))
			return names;

		// Final fallback: find JSON object in the response
		var jsonStart = responseBody.IndexOf('{');
		if (jsonStart < 0)
			return names;

		var jsonEnd = responseBody.LastIndexOf('}');
		if (jsonEnd > jsonStart)
			TryExtractToolNames(responseBody[jsonStart..(jsonEnd + 1)], names);

		return names;
	}

	private static JsonElement? ExtractJsonRpcFromResponse(string responseBody)
	{
		// Try SSE format first: "event: message\ndata: {json}\n\n"
		foreach (var line in responseBody.Split('\n'))
		{
			var trimmed = line.Trim();
			if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
			var json = trimmed["data:".Length..].Trim();
			try
			{
				using var doc = JsonDocument.Parse(json);
				return doc.RootElement.Clone();
			}
			catch { /* Not valid JSON */ }
		}

		// Fallback: try parsing the whole thing as JSON
		try
		{
			using var doc = JsonDocument.Parse(responseBody);
			return doc.RootElement.Clone();
		}
		catch { /* Not valid JSON */ }

		// Final fallback: find JSON object by brace matching
		var jsonStart = responseBody.IndexOf('{');
		if (jsonStart < 0)
			return null;

		var jsonEnd = responseBody.LastIndexOf('}');
		if (jsonEnd <= jsonStart)
			return null;

		try
		{
			var jsonStr = responseBody[jsonStart..(jsonEnd + 1)];
			using var doc = JsonDocument.Parse(jsonStr);
			return doc.RootElement.Clone();
		}
		catch { /* Not valid JSON */ }

		return null;
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
