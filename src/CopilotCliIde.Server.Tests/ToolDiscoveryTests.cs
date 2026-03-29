using System.ComponentModel;
using System.Reflection;
using CopilotCliIde.Server.Tools;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tests;

public class ToolDiscoveryTests
{
	// Tool names must match VS Code's Copilot Chat extension exactly — compatibility contract.
	private static readonly HashSet<string> _expectedToolNames =
	[
		"get_vscode_info",
		"get_selection",
		"open_diff",
		"close_diff",
		"get_diagnostics",
		"read_file",
		"update_session_name"
	];

	[Fact]
	public void AllToolTypes_HaveMcpServerToolTypeAttribute()
	{
		var toolTypes = typeof(AspNetMcpPipeServer).Assembly.GetTypes()
			.Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
			.ToList();

		Assert.Equal(7, toolTypes.Count);
	}

	[Fact]
	public void AllToolMethods_HaveMcpServerToolAttribute()
	{
		var toolMethods = GetAllToolMethods().ToList();

		Assert.Equal(7, toolMethods.Count);
	}

	[Fact]
	public void ToolNames_MatchVsCodeCopilotChatExtension()
	{
		var actualNames = GetAllToolMethods()
			.Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!)
			.ToHashSet();

		Assert.Equal(_expectedToolNames, actualNames);
	}

	[Theory]
	[InlineData(typeof(GetVsInfoTool), "get_vscode_info")]
	[InlineData(typeof(GetSelectionTool), "get_selection")]
	[InlineData(typeof(OpenDiffTool), "open_diff")]
	[InlineData(typeof(CloseDiffTool), "close_diff")]
	[InlineData(typeof(GetDiagnosticsTool), "get_diagnostics")]
	[InlineData(typeof(ReadFileTool), "read_file")]
	[InlineData(typeof(UpdateSessionNameTool), "update_session_name")]
	public void SpecificTool_HasCorrectName(Type toolType, string expectedName)
	{
		var method = toolType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
			.First(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

		var attr = method.GetCustomAttribute<McpServerToolAttribute>()!;
		Assert.Equal(expectedName, attr.Name);
	}

	[Fact]
	public void AllToolMethods_HaveDescriptions()
	{
		foreach (var method in GetAllToolMethods())
		{
			var desc = method.GetCustomAttribute<DescriptionAttribute>();
			Assert.NotNull(desc);
			Assert.False(string.IsNullOrWhiteSpace(desc.Description),
				$"Tool {method.DeclaringType!.Name}.{method.Name} has empty description");
		}
	}

	[Fact]
	public void AllToolParameters_HaveDescriptions()
	{
		foreach (var method in GetAllToolMethods())
		{
			foreach (var param in method.GetParameters())
			{
				// RpcClient is injected via DI, not a user parameter
				if (param.ParameterType == typeof(RpcClient))
					continue;
				// CancellationToken is infrastructure
				if (param.ParameterType == typeof(CancellationToken))
					continue;

				var desc = param.GetCustomAttribute<DescriptionAttribute>();
				Assert.NotNull(desc);
				Assert.False(string.IsNullOrWhiteSpace(desc.Description),
					$"Parameter {param.Name} on {method.DeclaringType!.Name}.{method.Name} has no description");
			}
		}
	}

	[Fact]
	public void OpenDiffTool_HasThreeParameters()
	{
		var method = typeof(OpenDiffTool).GetMethods()
			.First(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

		var userParams = method.GetParameters()
			.Where(p => p.ParameterType != typeof(RpcClient) && p.ParameterType != typeof(CancellationToken))
			.ToList();

		Assert.Equal(3, userParams.Count);
		Assert.Contains(userParams, p => p.Name == "original_file_path");
		Assert.Contains(userParams, p => p.Name == "new_file_contents");
		Assert.Contains(userParams, p => p.Name == "tab_name");
	}

	[Fact]
	public void ReadFileTool_HasOptionalParameters()
	{
		var method = typeof(ReadFileTool).GetMethods()
			.First(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

		var userParams = method.GetParameters()
			.Where(p => p.ParameterType != typeof(RpcClient))
			.ToList();

		Assert.Equal(3, userParams.Count);

		var startLine = userParams.First(p => p.Name == "startLine");
		Assert.True(startLine.HasDefaultValue);
		Assert.Null(startLine.DefaultValue);

		var maxLines = userParams.First(p => p.Name == "maxLines");
		Assert.True(maxLines.HasDefaultValue);
		Assert.Null(maxLines.DefaultValue);
	}

	[Fact]
	public void GetDiagnosticsTool_UriIsOptional()
	{
		var method = typeof(GetDiagnosticsTool).GetMethods()
			.First(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

		var uriParam = method.GetParameters().First(p => p.Name == "uri");
		Assert.True(uriParam.HasDefaultValue);
		Assert.Equal("", uriParam.DefaultValue);
	}

	[Fact]
	public void ToolTypes_AreSealed()
	{
		var toolTypes = typeof(AspNetMcpPipeServer).Assembly.GetTypes()
			.Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null);

		foreach (var type in toolTypes)
		{
			Assert.True(type.IsSealed, $"{type.Name} should be sealed");
		}
	}

	private static IEnumerable<MethodInfo> GetAllToolMethods()
	{
		return typeof(AspNetMcpPipeServer).Assembly.GetTypes()
			.Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
			.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic))
			.Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);
	}
}
