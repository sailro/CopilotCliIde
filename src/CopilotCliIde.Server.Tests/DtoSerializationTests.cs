using System.Text.Json;
using CopilotCliIde.Shared;

namespace CopilotCliIde.Server.Tests;

/// <summary>
/// Tests that shared DTOs serialize/deserialize correctly.
/// These DTOs cross the RPC boundary between VS and the MCP server.
/// </summary>
public class DtoSerializationTests
{
	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false,
	};

	[Fact]
	public void SelectionResult_RoundTrip()
	{
		var original = new SelectionResult
		{
			Current = true,
			FilePath = @"C:\src\Program.cs",
			FileUrl = "file:///C:/src/Program.cs",
			Text = "var x = 42;",
			Selection = new SelectionRange
			{
				Start = new SelectionPosition { Line = 10, Character = 4 },
				End = new SelectionPosition { Line = 10, Character = 15 },
				IsEmpty = false,
			},
		};

		var json = JsonSerializer.Serialize(original, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<SelectionResult>(json, _jsonOptions)!;

		Assert.Equal(original.Current, deserialized.Current);
		Assert.Equal(original.FilePath, deserialized.FilePath);
		Assert.Equal(original.FileUrl, deserialized.FileUrl);
		Assert.Equal(original.Text, deserialized.Text);
		Assert.False(deserialized.Selection!.IsEmpty);
		Assert.Equal(10, deserialized.Selection.Start!.Line);
		Assert.Equal(4, deserialized.Selection.Start.Character);
		Assert.Equal(10, deserialized.Selection.End!.Line);
		Assert.Equal(15, deserialized.Selection.End.Character);
	}

	[Fact]
	public void SelectionResult_Defaults()
	{
		var result = new SelectionResult();

		Assert.False(result.Current);
		Assert.Null(result.FilePath);
		Assert.Null(result.FileUrl);
		Assert.Null(result.Text);
		Assert.Null(result.Selection);
	}

	[Fact]
	public void DiffResult_RoundTrip()
	{
		var original = new DiffResult
		{
			Success = true,
			DiffId = "diff-001",
			OriginalFilePath = @"C:\src\file.cs",
			ProposedFilePath = @"C:\temp\proposed.cs",
			TabName = "Edit: file.cs",
			Message = "Diff opened",
			UserAction = "accepted",
		};

		var json = JsonSerializer.Serialize(original, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<DiffResult>(json, _jsonOptions)!;

		Assert.Equal(original.Success, deserialized.Success);
		Assert.Equal(original.DiffId, deserialized.DiffId);
		Assert.Equal(original.OriginalFilePath, deserialized.OriginalFilePath);
		Assert.Equal(original.TabName, deserialized.TabName);
		Assert.Equal(original.UserAction, deserialized.UserAction);
	}

	[Fact]
	public void DiffResult_FailureCase()
	{
		var result = new DiffResult
		{
			Success = false,
			Error = "File not found",
		};

		var json = JsonSerializer.Serialize(result, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<DiffResult>(json, _jsonOptions)!;

		Assert.False(deserialized.Success);
		Assert.Equal("File not found", deserialized.Error);
		Assert.Null(deserialized.DiffId);
	}

	[Fact]
	public void CloseDiffResult_RoundTrip()
	{
		var original = new CloseDiffResult
		{
			Success = true,
			AlreadyClosed = false,
			TabName = "Edit: file.cs",
			OriginalFilePath = @"C:\src\file.cs",
			Message = "Closed",
		};

		var json = JsonSerializer.Serialize(original, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<CloseDiffResult>(json, _jsonOptions)!;

		Assert.True(deserialized.Success);
		Assert.False(deserialized.AlreadyClosed);
		Assert.Equal(original.TabName, deserialized.TabName);
	}

	[Fact]
	public void CloseDiffResult_AlreadyClosedCase()
	{
		var result = new CloseDiffResult
		{
			Success = true,
			AlreadyClosed = true,
		};

		var json = JsonSerializer.Serialize(result, _jsonOptions);
		Assert.Contains("true", json);

		var deserialized = JsonSerializer.Deserialize<CloseDiffResult>(json, _jsonOptions)!;
		Assert.True(deserialized.AlreadyClosed);
	}

	[Fact]
	public void VsInfoResult_WithProjects()
	{
		var original = new VsInfoResult
		{
			IdeName = "Visual Studio",
			SolutionPath = @"C:\src\MySolution.sln",
			SolutionName = "MySolution",
			SolutionDirectory = @"C:\src",
			ProcessId = 12345,
			Projects =
			[
				new ProjectInfo { Name = "WebApp", FullName = @"C:\src\WebApp\WebApp.csproj" },
				new ProjectInfo { Name = "WebApp.Tests", FullName = @"C:\src\WebApp.Tests\WebApp.Tests.csproj" },
			],
		};

		var json = JsonSerializer.Serialize(original, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<VsInfoResult>(json, _jsonOptions)!;

		Assert.Equal("Visual Studio", deserialized.IdeName);
		Assert.Equal(2, deserialized.Projects!.Count);
		Assert.Equal("WebApp", deserialized.Projects[0].Name);
		Assert.Equal("WebApp.Tests", deserialized.Projects[1].Name);
		Assert.Equal(12345, deserialized.ProcessId);
	}

	[Fact]
	public void VsInfoResult_NullProjects()
	{
		var result = new VsInfoResult { IdeName = "VS" };

		var json = JsonSerializer.Serialize(result, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<VsInfoResult>(json, _jsonOptions)!;

		Assert.Null(deserialized.Projects);
	}

	[Fact]
	public void DiagnosticsResult_WithDiagnostics()
	{
		var original = new DiagnosticsResult
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
							Severity = "Error",
							Message = "CS0103: The name 'x' does not exist",
							Source = "WebApp",
							Code = "CS0103",
							Range = new DiagnosticRange
							{
								Start = new SelectionPosition { Line = 42, Character = 8 },
								End = new SelectionPosition { Line = 42, Character = 9 },
							},
						},
					],
				},
				new FileDiagnostics
				{
					Uri = "file:///C:/src/Helpers.cs",
					FilePath = @"C:\src\Helpers.cs",
					Diagnostics =
					[
						new DiagnosticItem
						{
							Severity = "Warning",
							Message = "CS0168: Variable declared but never used",
							Source = "WebApp",
							Code = "CS0168",
							Range = new DiagnosticRange
							{
								Start = new SelectionPosition { Line = 10, Character = 12 },
								End = new SelectionPosition { Line = 10, Character = 13 },
							},
						},
					],
				},
			],
		};

		var json = JsonSerializer.Serialize(original, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<DiagnosticsResult>(json, _jsonOptions)!;

		Assert.Equal(2, deserialized.Files!.Count);
		Assert.Equal("Error", deserialized.Files[0].Diagnostics![0]!.Severity);
		Assert.Equal(42, deserialized.Files[0].Diagnostics![0]!.Range!.Start!.Line);
		Assert.Equal("Warning", deserialized.Files[1].Diagnostics![0]!.Severity);
	}

	[Fact]
	public void DiagnosticsResult_ErrorCase()
	{
		var result = new DiagnosticsResult { Error = "Solution not loaded" };

		var json = JsonSerializer.Serialize(result, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<DiagnosticsResult>(json, _jsonOptions)!;

		Assert.Null(deserialized.Files);
		Assert.Equal("Solution not loaded", deserialized.Error);
	}

	[Fact]
	public void ReadFileResult_RoundTrip()
	{
		var original = new ReadFileResult
		{
			FilePath = @"C:\src\Program.cs",
			Content = "using System;\n\nclass Program { }",
			TotalLines = 3,
			StartLine = 1,
			LinesReturned = 3,
		};

		var json = JsonSerializer.Serialize(original, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<ReadFileResult>(json, _jsonOptions)!;

		Assert.Equal(original.FilePath, deserialized.FilePath);
		Assert.Equal(original.Content, deserialized.Content);
		Assert.Equal(original.TotalLines, deserialized.TotalLines);
		Assert.Equal(original.StartLine, deserialized.StartLine);
		Assert.Equal(original.LinesReturned, deserialized.LinesReturned);
	}

	[Fact]
	public void ReadFileResult_ErrorCase()
	{
		var result = new ReadFileResult { Error = "Access denied" };

		var json = JsonSerializer.Serialize(result, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<ReadFileResult>(json, _jsonOptions)!;

		Assert.Equal("Access denied", deserialized.Error);
		Assert.Null(deserialized.Content);
	}

	[Fact]
	public void SelectionNotification_FullPayload()
	{
		var original = new SelectionNotification
		{
			Text = "var x = 42;",
			FilePath = @"C:\src\Program.cs",
			FileUrl = "file:///C:/src/Program.cs",
			Selection = new SelectionRange
			{
				Start = new SelectionPosition { Line = 5, Character = 0 },
				End = new SelectionPosition { Line = 5, Character = 11 },
				IsEmpty = false,
			},
		};

		var json = JsonSerializer.Serialize(original, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<SelectionNotification>(json, _jsonOptions)!;

		Assert.Equal(original.Text, deserialized.Text);
		Assert.Equal(original.FilePath, deserialized.FilePath);
		Assert.Equal(5, deserialized.Selection!.Start!.Line);
		Assert.Equal(11, deserialized.Selection.End!.Character);
		Assert.False(deserialized.Selection.IsEmpty);
	}

	[Fact]
	public void SelectionNotification_NullSelection()
	{
		var notification = new SelectionNotification
		{
			Text = null,
			FilePath = null,
			Selection = null,
		};

		var json = JsonSerializer.Serialize(notification, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<SelectionNotification>(json, _jsonOptions)!;

		Assert.Null(deserialized.Selection);
	}

	[Fact]
	public void SelectionRange_Empty()
	{
		var range = new SelectionRange
		{
			Start = new SelectionPosition { Line = 3, Character = 7 },
			End = new SelectionPosition { Line = 3, Character = 7 },
			IsEmpty = true,
		};

		var json = JsonSerializer.Serialize(range, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<SelectionRange>(json, _jsonOptions)!;

		Assert.True(deserialized.IsEmpty);
		Assert.Equal(deserialized.Start!.Line, deserialized.End!.Line);
		Assert.Equal(deserialized.Start.Character, deserialized.End.Character);
	}

	[Fact]
	public void ProjectInfo_RoundTrip()
	{
		var original = new ProjectInfo
		{
			Name = "MyProject",
			FullName = @"C:\repos\MySolution\MyProject\MyProject.csproj",
		};

		var json = JsonSerializer.Serialize(original, _jsonOptions);
		var deserialized = JsonSerializer.Deserialize<ProjectInfo>(json, _jsonOptions)!;

		Assert.Equal(original.Name, deserialized.Name);
		Assert.Equal(original.FullName, deserialized.FullName);
	}
}
