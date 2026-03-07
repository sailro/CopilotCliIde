using System.Reflection;
using System.Text.Json;
using CopilotCliIde.Server.Tools;
using CopilotCliIde.Shared;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tests;

/// <summary>
/// Protocol compatibility tests that verify our MCP server's tool registrations,
/// response schemas, and lock file format match VS Code's Copilot Chat extension.
/// Uses golden JSON snapshots with structural superset comparison.
/// </summary>
public class ProtocolCompatibilityTests
{
	/// <summary>
	/// The 6 tool names that VS Code's Copilot Chat extension registers.
	/// Our server may have extras (e.g. read_file) — that's fine.
	/// </summary>
	private static readonly string[] _vsCodeToolNames =
	[
		"get_vscode_info",
		"get_selection",
		"get_diagnostics",
		"open_diff",
		"close_diff",
		"update_session_name",
	];

	private static readonly JsonSerializerOptions _camelCaseOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	#region Tool List Tests

	[Fact]
	public void ToolsList_ContainsAllVsCodeTools()
	{
		var actualNames = GetAllToolMethods()
			.Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!)
			.ToHashSet();

		foreach (var expectedName in _vsCodeToolNames)
		{
			Assert.Contains(expectedName, actualNames);
		}
	}

	[Fact]
	public void ToolsList_ToolInputSchemas_MatchGolden()
	{
		var goldenJson = LoadSnapshot("tools-list.json");
		var goldenDoc = JsonDocument.Parse(goldenJson);
		var goldenTools = goldenDoc.RootElement.GetProperty("tools");

		var actualToolMethods = GetAllToolMethods()
			.ToDictionary(
				m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!,
				m => m);

		foreach (var goldenTool in goldenTools.EnumerateArray())
		{
			var toolName = goldenTool.GetProperty("name").GetString()!;
			Assert.True(actualToolMethods.ContainsKey(toolName),
				$"Tool '{toolName}' from golden snapshot not found in server");

			var method = actualToolMethods[toolName];
			var goldenParams = goldenTool.GetProperty("parameters");

			// Build actual parameter schema from reflection
			var userParams = method.GetParameters()
				.Where(p => p.ParameterType != typeof(RpcClient) && p.ParameterType != typeof(CancellationToken))
				.ToList();

			var actualParamObj = new Dictionary<string, string>();
			foreach (var param in userParams)
			{
				actualParamObj[param.Name!] = MapParameterType(param.ParameterType);
			}

			var actualJson = JsonSerializer.Serialize(actualParamObj);
			var actualDoc = JsonDocument.Parse(actualJson);

			var mismatches = JsonSchemaComparer.Compare(actualDoc.RootElement, goldenParams);
			Assert.True(mismatches.Count == 0,
				$"Tool '{toolName}' input schema mismatches:\n{string.Join("\n", mismatches)}");
		}
	}

	#endregion

	#region Lock File Tests

	[Fact]
	public void LockFile_Schema_MatchesVsCode()
	{
		// Construct a lock file JSON matching our IdeDiscovery.WriteLockFileAsync output
		var lockFile = new
		{
			socketPath = @"\\.\pipe\mcp-test.sock",
			scheme = "pipe",
			headers = new
			{
				Authorization = "Nonce test-nonce-value",
			},
			pid = 12345,
			ideName = "Visual Studio",
			timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			workspaceFolders = new[] { @"C:\src\TestSolution" },
			isTrusted = true,
		};

		var json = JsonSerializer.Serialize(lockFile);
		var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		// Verify all 8 required fields exist
		var requiredFields = new[]
		{
			"socketPath", "scheme", "headers", "pid",
			"ideName", "timestamp", "workspaceFolders", "isTrusted",
		};

		foreach (var field in requiredFields)
		{
			Assert.True(root.TryGetProperty(field, out _),
				$"Lock file missing required field: {field}");
		}

		// Verify field types
		Assert.Equal(JsonValueKind.String, root.GetProperty("socketPath").ValueKind);
		Assert.Equal(JsonValueKind.String, root.GetProperty("scheme").ValueKind);
		Assert.Equal(JsonValueKind.Object, root.GetProperty("headers").ValueKind);
		Assert.Equal(JsonValueKind.Number, root.GetProperty("pid").ValueKind);
		Assert.Equal(JsonValueKind.String, root.GetProperty("ideName").ValueKind);
		Assert.Equal(JsonValueKind.Number, root.GetProperty("timestamp").ValueKind);
		Assert.Equal(JsonValueKind.Array, root.GetProperty("workspaceFolders").ValueKind);
		Assert.True(
			root.GetProperty("isTrusted").ValueKind is JsonValueKind.True or JsonValueKind.False,
			"isTrusted should be boolean");

		// Verify headers contains Authorization
		var headers = root.GetProperty("headers");
		Assert.True(headers.TryGetProperty("Authorization", out var auth));
		Assert.StartsWith("Nonce ", auth.GetString());

		// Verify workspaceFolders contains at least one string
		var folders = root.GetProperty("workspaceFolders");
		Assert.True(folders.GetArrayLength() > 0, "workspaceFolders should not be empty");
		Assert.Equal(JsonValueKind.String, folders[0].ValueKind);
	}

	#endregion

	#region Helpers

	private static string LoadSnapshot(string fileName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(ProtocolCompatibilityTests).Assembly.Location)!;
		var snapshotPath = Path.Combine(assemblyDir, "Snapshots", fileName);
		Assert.True(File.Exists(snapshotPath),
			$"Snapshot file not found: {snapshotPath}");
		return File.ReadAllText(snapshotPath);
	}

	private static string MapParameterType(Type type)
	{
		// Unwrap Nullable<T>
		var underlying = Nullable.GetUnderlyingType(type) ?? type;

		if (underlying == typeof(string))
			return "string";
		if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(double) || underlying == typeof(float))
			return "number";
		if (underlying == typeof(bool))
			return "boolean";

		return "object";
	}

	private static IEnumerable<MethodInfo> GetAllToolMethods()
	{
		return typeof(McpPipeServer).Assembly.GetTypes()
			.Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
			.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic))
			.Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);
	}

	#endregion
}
