using System.Text.Json;

namespace CopilotCliIde.Server.Tests;

// Strict cross-capture consistency tests. VS Code captures are the reference.
// If we don't match, tests fail. No exceptions.
// Only deliberate exception: read_file is a VS-specific extra tool.
public class CrossCaptureConsistencyTests
{
	private const string VsCapturePrefix = "vs-"; // our capture starts with "vs-", everything else is VS Code
	private const string AllowedExtraTool = "read_file"; // VS-specific extra tool, excluded from "extra tool" failures

	// MCP SDK-level differences not in our control — excluded explicitly so they don't mask real regressions.
	// logging: C# MCP SDK auto-registers it, VS Code's SDK does not.
	// additionalProperties: VS Code's SDK emits "additionalProperties": false, C# SDK does not.
	private static readonly HashSet<string> _mcpSdkExtraCapabilities = ["logging"];
	private const bool McpSdkOmitsAdditionalProperties = true;

	#region Tool Response Field Consistency

	[Fact]
	public void ToolResponseFields_ExactMatchWithVsCode()
	{
		var vsParser = LoadVsCapture();
		var vsCodeParsers = LoadVsCodeCaptures();

		var vsCodeFieldsByTool = new Dictionary<string, HashSet<string>>();
		foreach (var (_, parser) in vsCodeParsers)
		{
			foreach (var toolName in GetAllVsCodeToolNames())
			{
				var fields = ExtractToolResponseFields(parser, toolName);
				if (fields is null)
					continue;

				if (!vsCodeFieldsByTool.ContainsKey(toolName))
					vsCodeFieldsByTool[toolName] = [];
				vsCodeFieldsByTool[toolName].UnionWith(fields);
			}
		}

		var errors = new List<string>();

		foreach (var (toolName, vsCodeFields) in vsCodeFieldsByTool)
		{
			var vsFields = ExtractToolResponseFields(vsParser, toolName);
			if (vsFields is null)
			{
				errors.Add($"  {toolName}: No response found in VS capture");
				continue;
			}

			errors.AddRange(vsCodeFields.Except(vsFields)
				.Select(field => $"  {toolName}: MISSING field '{field}' (VS Code has it, we don't)"));

			errors.AddRange(vsFields.Except(vsCodeFields)
				.Select(field => $"  {toolName}: EXTRA field '{field}' (we have it, VS Code doesn't)"));
		}

		Assert.True(errors.Count == 0,
			$"Tool response fields do not exactly match VS Code:\n{string.Join("\n", errors)}");
	}

	#endregion

	#region Notification Field Consistency

	[Fact]
	public void SelectionChangedNotification_ExactMatchWithVsCode()
	{
		var vsParser = LoadVsCapture();
		var vsCodeParsers = LoadVsCodeCaptures();

		var vsCodeFields = new HashSet<string>();
		foreach (var (_, parser) in vsCodeParsers)
		{
			var fields = ExtractNotificationParamsFields(parser, "selection_changed");
			if (fields is not null)
				vsCodeFields.UnionWith(fields);
		}

		Assert.True(vsCodeFields.Count > 0, "No selection_changed notifications found in VS Code captures");

		var vsFields = ExtractNotificationParamsFields(vsParser, "selection_changed");
		Assert.NotNull(vsFields);

		var errors = new List<string>();

		errors.AddRange(vsCodeFields.Except(vsFields)
			.Select(f => $"  MISSING: '{f}' (VS Code has it, we don't)"));
		errors.AddRange(vsFields.Except(vsCodeFields)
			.Select(f => $"  EXTRA: '{f}' (we have it, VS Code doesn't)"));

		Assert.True(errors.Count == 0,
			$"selection_changed params do not exactly match VS Code:\n{string.Join("\n", errors)}");
	}

	[Fact]
	public void DiagnosticsChangedNotification_DiagnosticFields_ExactMatchWithVsCode()
	{
		var vsParser = LoadVsCapture();
		var vsCodeParsers = LoadVsCodeCaptures();

		var vsCodeDiagFields = new HashSet<string>();
		foreach (var (_, parser) in vsCodeParsers)
		{
			var fields = ExtractDiagnosticItemFields(parser);
			if (fields is not null)
				vsCodeDiagFields.UnionWith(fields);
		}

		Assert.True(vsCodeDiagFields.Count > 0,
			"No diagnostics_changed notifications with diagnostic items found in VS Code captures");

		var vsDiagFields = ExtractDiagnosticItemFields(vsParser);
		Assert.NotNull(vsDiagFields);

		var errors = new List<string>();

		errors.AddRange(vsCodeDiagFields.Except(vsDiagFields)
			.Select(f => $"  MISSING: '{f}' (VS Code has it, we don't)"));
		errors.AddRange(vsDiagFields.Except(vsCodeDiagFields)
			.Select(f => $"  EXTRA: '{f}' (we have it, VS Code doesn't)"));

		Assert.True(errors.Count == 0,
			$"diagnostics_changed diagnostic item fields do not exactly match VS Code:\n" +
			$"{string.Join("\n", errors)}\n" +
			$"\nVS Code fields: [{string.Join(", ", vsCodeDiagFields.Order())}]" +
			$"\nVS fields:      [{string.Join(", ", vsDiagFields.Order())}]");
	}

	[Fact]
	public void DiagnosticsChangedNotification_RangeEnd_MustNotBeZeroed()
	{
		var vsCodeParsers = LoadVsCodeCaptures();
		var vsParser = LoadVsCapture();

		var vsCodeEnds = CollectRangeEndValues(vsCodeParsers.Select(p => p.Parser));
		var vsEnds = CollectRangeEndValues([vsParser]);

		var vsCodeHasNonZeroEnd = vsCodeEnds.Any(e => e.Line != 0 || e.Character != 0);
		var vsHasNonZeroEnd = vsEnds.Any(e => e.Line != 0 || e.Character != 0);

		var vsCodeNonZeroCount = vsCodeEnds.Count(e => e.Line != 0 || e.Character != 0);
		var vsNonZeroCount = vsEnds.Count(e => e.Line != 0 || e.Character != 0);

		Assert.True(!vsCodeHasNonZeroEnd || vsHasNonZeroEnd,
			$"VS Code has {vsCodeNonZeroCount}/{vsCodeEnds.Count} non-zero range.end values, " +
			$"but VS has {vsNonZeroCount}/{vsEnds.Count}. " +
			$"We must return real end positions, not zeros.");
	}

	#endregion

	#region Initialize Response Consistency

	[Fact]
	public void InitializeResponse_ExactMatchWithVsCode()
	{
		var vsParser = LoadVsCapture();
		var vsCodeParsers = LoadVsCodeCaptures();

		var vsCodeResultFields = new HashSet<string>();
		var vsCodeCapabilityFields = new HashSet<string>();
		var vsCodeServerInfoFields = new HashSet<string>();

		foreach (var (_, parser) in vsCodeParsers)
		{
			var initResp = parser.GetInitializeResponse();
			if (initResp is null)
				continue;

			var result = initResp.Value.GetProperty("result");
			foreach (var prop in result.EnumerateObject())
				vsCodeResultFields.Add(prop.Name);

			if (result.TryGetProperty("capabilities", out var caps))
				foreach (var prop in caps.EnumerateObject())
					vsCodeCapabilityFields.Add(prop.Name);

			if (!result.TryGetProperty("serverInfo", out var si))
				continue;

			foreach (var prop in si.EnumerateObject())
				vsCodeServerInfoFields.Add(prop.Name);
		}

		Assert.True(vsCodeResultFields.Count > 0, "No initialize responses found in VS Code captures");

		var vsInitResp = vsParser.GetInitializeResponse();
		Assert.NotNull(vsInitResp);

		var vsResult = vsInitResp.Value.GetProperty("result");
		var vsResultFields = vsResult.EnumerateObject().Select(p => p.Name).ToHashSet();

		var vsCapabilityFields = new HashSet<string>();
		if (vsResult.TryGetProperty("capabilities", out var vsCaps))
			foreach (var prop in vsCaps.EnumerateObject())
				vsCapabilityFields.Add(prop.Name);

		var vsServerInfoFields = new HashSet<string>();
		if (vsResult.TryGetProperty("serverInfo", out var vsSi))
			foreach (var prop in vsSi.EnumerateObject())
				vsServerInfoFields.Add(prop.Name);

		var errors = new List<string>();

		// Result-level: exact match
		errors.AddRange(vsCodeResultFields.Except(vsResultFields)
			.Select(f => $"  result: MISSING '{f}'"));
		errors.AddRange(vsResultFields.Except(vsCodeResultFields)
			.Select(f => $"  result: EXTRA '{f}' (VS Code doesn't have it)"));

		// Capabilities: exact match (except MCP SDK extras)
		errors.AddRange(vsCodeCapabilityFields.Except(vsCapabilityFields)
			.Select(f => $"  capabilities: MISSING '{f}'"));
		errors.AddRange(vsCapabilityFields.Except(vsCodeCapabilityFields)
			.Where(f => !_mcpSdkExtraCapabilities.Contains(f))
			.Select(f => $"  capabilities: EXTRA '{f}' (VS Code doesn't have it)"));

		// ServerInfo: exact match
		errors.AddRange(vsCodeServerInfoFields.Except(vsServerInfoFields)
			.Select(f => $"  serverInfo: MISSING '{f}'"));
		errors.AddRange(vsServerInfoFields.Except(vsCodeServerInfoFields)
			.Select(f => $"  serverInfo: EXTRA '{f}' (VS Code doesn't have it)"));

		Assert.True(errors.Count == 0,
			$"Initialize response does not exactly match VS Code:\n{string.Join("\n", errors)}");
	}

	#endregion

	#region Tool Input Schema Consistency

	[Fact]
	public void ToolInputSchemas_StrictMatchWithVsCode()
	{
		var vsParser = LoadVsCapture();
		var vsCodeParsers = LoadVsCodeCaptures();

		var vsCodeSchemas = new Dictionary<string, JsonElement>();
		foreach (var (_, parser) in vsCodeParsers)
		{
			var toolsList = parser.GetToolsListResponse();
			if (toolsList is null)
				continue;

			var tools = toolsList.Value.GetProperty("result").GetProperty("tools");
			foreach (var tool in tools.EnumerateArray())
			{
				var name = tool.GetProperty("name").GetString()!;
				if (tool.TryGetProperty("inputSchema", out var schema))
					vsCodeSchemas[name] = schema.Clone();
			}
		}

		var vsToolsList = vsParser.GetToolsListResponse();
		Assert.NotNull(vsToolsList);

		var vsSchemas = new Dictionary<string, JsonElement>();
		var vsTools = vsToolsList.Value.GetProperty("result").GetProperty("tools");
		foreach (var tool in vsTools.EnumerateArray())
		{
			var name = tool.GetProperty("name").GetString()!;
			if (tool.TryGetProperty("inputSchema", out var schema))
				vsSchemas[name] = schema.Clone();
		}

		var errors = new List<string>();

		// VS Code tools missing from VS → FAIL
		foreach (var (toolName, vsCodeSchema) in vsCodeSchemas)
		{
			if (!vsSchemas.TryGetValue(toolName, out var vsSchema))
			{
				errors.Add($"  {toolName}: Entire tool MISSING from VS tools/list");
				continue;
			}

			CompareSchemas(toolName, vsCodeSchema, vsSchema, errors);
		}

		// VS tools not in VS Code → FAIL (except read_file)
		errors.AddRange(vsSchemas.Keys.Except(vsCodeSchemas.Keys)
			.Where(toolName => toolName != AllowedExtraTool)
			.Select(toolName => $"  {toolName}: EXTRA tool in VS (VS Code doesn't have it)"));

		Assert.True(errors.Count == 0,
			$"Tool input schemas do not strictly match VS Code:\n{string.Join("\n", errors)}");
	}

	private static void CompareSchemas(string toolName, JsonElement vsCode, JsonElement vs, List<string> errors)
	{
		// Compare properties
		var vsCodeProps = GetSchemaProperties(vsCode);
		var vsProps = GetSchemaProperties(vs);

		foreach (var (propName, propType) in vsCodeProps)
		{
			if (!vsProps.TryGetValue(propName, out var vsType))
				errors.Add($"  {toolName}: MISSING schema property '{propName}' (type: {propType})");
			else if (vsType != propType)
				errors.Add($"  {toolName}.{propName}: type mismatch — VS Code: {propType}, VS: {vsType}");
		}

		errors.AddRange(vsProps.Keys.Except(vsCodeProps.Keys)
			.Select(propName => $"  {toolName}: EXTRA schema property '{propName}' (VS Code doesn't have it)"));

		// Compare required
		var vsCodeRequired = GetSchemaRequired(vsCode);
		var vsRequired = GetSchemaRequired(vs);

		errors.AddRange(vsCodeRequired.Except(vsRequired)
			.Select(r => $"  {toolName}: MISSING required field '{r}'"));
		errors.AddRange(vsRequired.Except(vsCodeRequired)
			.Select(r => $"  {toolName}: EXTRA required field '{r}'"));

		// Compare additionalProperties (skip if MCP SDK is known to omit it)
		var vsCodeAdditional = GetAdditionalProperties(vsCode);
		var vsAdditional = GetAdditionalProperties(vs);
		if (vsCodeAdditional == vsAdditional)
			return;

		var isSdkOmission = McpSdkOmitsAdditionalProperties && vsAdditional is null && vsCodeAdditional is not null;
		if (!isSdkOmission)
			errors.Add($"  {toolName}: additionalProperties mismatch — VS Code: {vsCodeAdditional ?? "absent"}, VS: {vsAdditional ?? "absent"}");
	}

	private static Dictionary<string, string> GetSchemaProperties(JsonElement schema)
	{
		var result = new Dictionary<string, string>();
		if (!schema.TryGetProperty("properties", out var props))
			return result;

		foreach (var prop in props.EnumerateObject())
		{
			var typeStr = prop.Value.TryGetProperty("type", out var t) ? t.ToString() : "unknown";
			result[prop.Name] = typeStr;
		}
		return result;
	}

	private static HashSet<string> GetSchemaRequired(JsonElement schema)
	{
		var result = new HashSet<string>();
		if (!schema.TryGetProperty("required", out var req))
			return result;

		foreach (var r in req.EnumerateArray())
			result.Add(r.GetString()!);
		return result;
	}

	private static string? GetAdditionalProperties(JsonElement schema) =>
		schema.TryGetProperty("additionalProperties", out var ap) ? ap.ToString() : null;

	#endregion

	#region Helpers — Capture Loading

	private static TrafficParser LoadVsCapture()
	{
		var dir = FindCapturesDir();
		var files = Directory.GetFiles(dir, "*.ndjson")
			.Where(f => Path.GetFileName(f).StartsWith(VsCapturePrefix, StringComparison.OrdinalIgnoreCase))
			.ToList();
		Assert.True(files.Count == 1,
			$"Expected exactly 1 VS capture (starting with '{VsCapturePrefix}'), found {files.Count} in {dir}");
		return TrafficParser.Load(files[0]);
	}

	private static List<(string Name, TrafficParser Parser)> LoadVsCodeCaptures()
	{
		var dir = FindCapturesDir();
		var result = Directory.GetFiles(dir, "*.ndjson")
			.Where(f => !Path.GetFileName(f).StartsWith(VsCapturePrefix, StringComparison.OrdinalIgnoreCase))
			.Select(f => (Path.GetFileName(f), TrafficParser.Load(f)))
			.ToList();
		Assert.True(result.Count > 0, "No VS Code reference captures found");
		return result;
	}

	private static string[] GetAllVsCodeToolNames() =>
	[
		"get_selection",
		"get_diagnostics",
		"get_vscode_info",
		"open_diff",
		"close_diff",
		"update_session_name"
	];

	#endregion

	#region Helpers — Field Extraction

	private static HashSet<string>? ExtractToolResponseFields(TrafficParser parser, string toolName)
	{
		var responses = parser.GetAllToolCallResponses(toolName);
		if (responses.Count == 0)
			return null;

		var fields = new HashSet<string>();
		foreach (var response in responses)
		{
			var innerJson = ExtractInnerTextJson(response);
			if (innerJson is null)
				continue;

			switch (innerJson.Value.ValueKind)
			{
				case JsonValueKind.Object:
					foreach (var prop in innerJson.Value.EnumerateObject())
						fields.Add(prop.Name);
					break;
				case JsonValueKind.Array:
					foreach (var item in innerJson.Value.EnumerateArray())
					{
						if (item.ValueKind != JsonValueKind.Object)
							continue;
						foreach (var prop in item.EnumerateObject())
							fields.Add(prop.Name);
					}
					break;
			}
		}

		return fields.Count > 0 ? fields : null;
	}

	private static JsonElement? ExtractInnerTextJson(JsonElement response)
	{
		if (!response.TryGetProperty("result", out var result))
			return null;
		if (!result.TryGetProperty("content", out var content))
			return null;
		if (content.GetArrayLength() == 0)
			return null;

		var text = content[0].GetProperty("text").GetString();
		if (text is null or "null" or "[]")
			return null;

		try
		{
			using var doc = JsonDocument.Parse(text);
			return doc.RootElement.Clone();
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static HashSet<string>? ExtractNotificationParamsFields(TrafficParser parser, string method)
	{
		var notifications = parser.GetNotifications(method);
		if (notifications.Count == 0)
			return null;

		var fields = new HashSet<string>();
		foreach (var notification in notifications)
		{
			if (!notification.TryGetProperty("params", out var @params))
				continue;

			foreach (var prop in @params.EnumerateObject())
				fields.Add(prop.Name);
		}

		return fields.Count > 0 ? fields : null;
	}

	private static HashSet<string>? ExtractDiagnosticItemFields(TrafficParser parser)
	{
		var notifications = parser.GetNotifications("diagnostics_changed");
		if (notifications.Count == 0)
			return null;

		var fields = new HashSet<string>();
		foreach (var notification in notifications)
		{
			if (!notification.TryGetProperty("params", out var @params))
				continue;
			if (!@params.TryGetProperty("uris", out var uris))
				continue;

			foreach (var uriEntry in uris.EnumerateArray())
			{
				if (!uriEntry.TryGetProperty("diagnostics", out var diags))
					continue;

				foreach (var diag in diags.EnumerateArray())
				{
					if (diag.ValueKind != JsonValueKind.Object)
						continue;
					foreach (var prop in diag.EnumerateObject())
						fields.Add(prop.Name);
				}
			}
		}

		return fields.Count > 0 ? fields : null;
	}

	private static List<(int Line, int Character)> CollectRangeEndValues(IEnumerable<TrafficParser> parsers)
	{
		var ends = new List<(int Line, int Character)>();
		foreach (var parser in parsers)
		{
			var notifications = parser.GetNotifications("diagnostics_changed");
			foreach (var notification in notifications)
			{
				if (!notification.TryGetProperty("params", out var @params))
					continue;
				if (!@params.TryGetProperty("uris", out var uris))
					continue;

				foreach (var uriEntry in uris.EnumerateArray())
				{
					if (!uriEntry.TryGetProperty("diagnostics", out var diags))
						continue;

					foreach (var diag in diags.EnumerateArray())
					{
						if (!diag.TryGetProperty("range", out var range))
							continue;
						if (!range.TryGetProperty("end", out var end))
							continue;

						var line = end.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
						var character = end.TryGetProperty("character", out var c) ? c.GetInt32() : 0;
						ends.Add((line, character));
					}
				}
			}
		}

		return ends;
	}

	#endregion

	#region Helpers — Capture Directory

	private static string FindCapturesDir()
	{
		var assemblyDir = Path.GetDirectoryName(typeof(CrossCaptureConsistencyTests).Assembly.Location)!;

		var fromAssembly = Path.Combine(assemblyDir, "Captures");
		if (Directory.Exists(fromAssembly))
			return fromAssembly;

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

	#endregion
}
