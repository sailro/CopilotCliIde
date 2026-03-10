using System.Text.Json;
using CopilotCliIde.Shared;

namespace CopilotCliIde.Server.Tests;

public class ToolOutputSchemaTests
{
	private static readonly JsonSerializerOptions _camelCaseOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	[Fact]
	public void OpenDiff_Output_HasSnakeCaseKeys()
	{
		// Simulate what OpenDiffTool does with the DiffResult
		var rpcResult = new DiffResult
		{
			Success = true,
			Result = DiffOutcome.Saved,
			Trigger = DiffTrigger.AcceptedViaButton,
			TabName = "Edit: file.cs",
			Message = "Changes saved",
			Error = null
		};

		var toolOutput = new
		{
			success = rpcResult.Success,
			result = rpcResult.Result,
			trigger = rpcResult.Trigger,
			tab_name = rpcResult.TabName,
			message = rpcResult.Message,
			error = rpcResult.Error
		};

		var json = JsonSerializer.Serialize(toolOutput);
		var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		// Must use snake_case keys, not camelCase
		Assert.True(root.TryGetProperty("tab_name", out _));
		Assert.False(root.TryGetProperty("tabName", out _));
		Assert.True(root.TryGetProperty("success", out _));
		Assert.True(root.TryGetProperty("result", out var result));
		Assert.True(root.TryGetProperty("trigger", out var trigger));

		Assert.Equal(DiffOutcome.Saved, result.GetString());
		Assert.Equal(DiffTrigger.AcceptedViaButton, trigger.GetString());
	}

	[Fact]
	public void OpenDiff_Output_RejectedResult()
	{
		var rpcResult = new DiffResult
		{
			Success = true,
			Result = DiffOutcome.Rejected,
			Trigger = DiffTrigger.RejectedViaButton,
			TabName = "Edit: file.cs",
			Message = "Changes rejected"
		};

		var toolOutput = new
		{
			success = rpcResult.Success,
			result = rpcResult.Result,
			trigger = rpcResult.Trigger,
			tab_name = rpcResult.TabName,
			message = rpcResult.Message,
			error = rpcResult.Error
		};

		var json = JsonSerializer.Serialize(toolOutput);
		var doc = JsonDocument.Parse(json);

		Assert.Equal(DiffOutcome.Rejected, doc.RootElement.GetProperty("result").GetString());
		Assert.Equal(DiffTrigger.RejectedViaButton, doc.RootElement.GetProperty("trigger").GetString());
	}

	[Fact]
	public void OpenDiff_Output_ClosedViaTool_Rejection()
	{
		var rpcResult = new DiffResult
		{
			Success = true,
			Result = DiffOutcome.Rejected,
			Trigger = DiffTrigger.ClosedViaTool,
			TabName = "Edit: file.cs"
		};

		var toolOutput = new
		{
			success = rpcResult.Success,
			result = rpcResult.Result,
			trigger = rpcResult.Trigger,
			tab_name = rpcResult.TabName,
			message = rpcResult.Message,
			error = rpcResult.Error
		};

		var json = JsonSerializer.Serialize(toolOutput);
		var doc = JsonDocument.Parse(json);

		Assert.Equal(DiffTrigger.ClosedViaTool, doc.RootElement.GetProperty("trigger").GetString());
	}

	[Fact]
	public void OpenDiff_Output_ClosedViaTool()
	{
		var rpcResult = new DiffResult
		{
			Success = true,
			Result = DiffOutcome.Rejected,
			Trigger = DiffTrigger.ClosedViaTool,
			TabName = "Edit: file.cs"
		};

		var toolOutput = new
		{
			success = rpcResult.Success,
			result = rpcResult.Result,
			trigger = rpcResult.Trigger,
			tab_name = rpcResult.TabName,
			message = rpcResult.Message,
			error = rpcResult.Error
		};

		var json = JsonSerializer.Serialize(toolOutput);
		var doc = JsonDocument.Parse(json);

		Assert.Equal(DiffTrigger.ClosedViaTool, doc.RootElement.GetProperty("trigger").GetString());
		Assert.Equal(DiffOutcome.Rejected, doc.RootElement.GetProperty("result").GetString());
	}

	[Fact]
	public void OpenDiff_Output_ErrorCase()
	{
		var rpcResult = new DiffResult
		{
			Success = false,
			Error = "File not found"
		};

		var toolOutput = new
		{
			success = rpcResult.Success,
			result = rpcResult.Result,
			trigger = rpcResult.Trigger,
			tab_name = rpcResult.TabName,
			message = rpcResult.Message,
			error = rpcResult.Error
		};

		var json = JsonSerializer.Serialize(toolOutput);
		var doc = JsonDocument.Parse(json);

		Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
		Assert.Equal("File not found", doc.RootElement.GetProperty("error").GetString());
		Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("result").ValueKind);
		Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("trigger").ValueKind);
	}

	[Fact]
	public void CloseDiff_Output_HasSnakeCaseKeys()
	{
		var rpcResult = new CloseDiffResult
		{
			Success = true,
			AlreadyClosed = false,
			TabName = "Edit: file.cs",
			Message = "Tab closed",
			Error = null
		};

		var toolOutput = new
		{
			success = rpcResult.Success,
			already_closed = rpcResult.AlreadyClosed,
			tab_name = rpcResult.TabName,
			message = rpcResult.Message,
			error = rpcResult.Error
		};

		var json = JsonSerializer.Serialize(toolOutput);
		var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		// Must use snake_case
		Assert.True(root.TryGetProperty("already_closed", out _));
		Assert.False(root.TryGetProperty("alreadyClosed", out _));
		Assert.True(root.TryGetProperty("tab_name", out _));
		Assert.False(root.TryGetProperty("tabName", out _));
	}

	[Fact]
	public void CloseDiff_Output_AlreadyClosed()
	{
		var rpcResult = new CloseDiffResult
		{
			Success = true,
			AlreadyClosed = true,
			TabName = "Edit: file.cs",
			Message = "Tab was already closed"
		};

		var toolOutput = new
		{
			success = rpcResult.Success,
			already_closed = rpcResult.AlreadyClosed,
			tab_name = rpcResult.TabName,
			message = rpcResult.Message,
			error = rpcResult.Error
		};

		var json = JsonSerializer.Serialize(toolOutput);
		var doc = JsonDocument.Parse(json);

		Assert.True(doc.RootElement.GetProperty("already_closed").GetBoolean());
	}

	[Fact]
	public void GetDiagnostics_Output_ReturnsFilesArray()
	{
		// GetDiagnosticsTool returns result.Files directly (not wrapped)
		var rpcResult = new DiagnosticsResult
		{
			Files =
			[
				new FileDiagnostics
				{
					Uri = "file:///C:/src/Program.cs",
					FilePath = @"C:\src\Program.cs",
					Diagnostics =
					[
						new DiagnosticItem
						{
							Severity = "error",
							Message = "CS0103",
							Range = new DiagnosticRange
							{
								Start = new SelectionPosition { Line = 5, Character = 0 },
								End = new SelectionPosition { Line = 5, Character = 3 }
							},
							Code = "CS0103"
						}
					]
				}
			]
		};

		// Tool returns: result.Files ?? []
		var toolOutput = rpcResult.Files ?? [];
		var json = JsonSerializer.Serialize(toolOutput, _camelCaseOptions);

		// Should be a JSON array at root (not an object with a "files" key)
		var doc = JsonDocument.Parse(json);
		Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
		Assert.Equal(1, doc.RootElement.GetArrayLength());

		var file = doc.RootElement[0];
		Assert.Equal("file:///C:/src/Program.cs", file.GetProperty("uri").GetString());
		Assert.Equal(1, file.GetProperty("diagnostics").GetArrayLength());
	}

	[Fact]
	public void GetDiagnostics_Output_ErrorReturnsObject()
	{
		var rpcResult = new DiagnosticsResult { Error = "Solution not loaded" };

		// Tool returns: new { error = result.Error }
		var toolOutput = new { error = rpcResult.Error };
		var json = JsonSerializer.Serialize(toolOutput);
		var doc = JsonDocument.Parse(json);

		Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
		Assert.Equal("Solution not loaded", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public void GetDiagnostics_Output_NullFiles_ReturnsEmptyArray()
	{
		var rpcResult = new DiagnosticsResult { Files = null };

		// Tool returns: result.Files ?? []
		var toolOutput = rpcResult.Files ?? [];
		var json = JsonSerializer.Serialize(toolOutput, _camelCaseOptions);

		var doc = JsonDocument.Parse(json);
		Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
		Assert.Equal(0, doc.RootElement.GetArrayLength());
	}
}
