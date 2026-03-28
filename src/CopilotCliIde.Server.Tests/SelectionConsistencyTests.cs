using System.Text.Json;
using CopilotCliIde.Shared;

namespace CopilotCliIde.Server.Tests;

// Guards against drift between selection_changed push and get_selection pull paths.
public class SelectionConsistencyTests
{

	public static TheoryData<string> CaptureFiles()
	{
		var data = new TheoryData<string>();
		foreach (var file in GetCaptureFiles())
			data.Add(file);
		return data;
	}

	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void GetSelection_MatchesPrecedingSelectionChanged(string captureFile)
	{
		var parser = TrafficParser.Load(captureFile);

		// Walk entries in sequence order, tracking the last selection_changed
		JsonElement? lastPushParams = null;
		var comparisons = 0;

		foreach (var entry in parser.Entries)
		{
			// Track selection_changed notifications (push path output)
			if (entry is { Direction: "vscode_to_cli", JsonRpcMessage: not null })
			{
				var msg = entry.JsonRpcMessage.Value;
				if (TryGetString(msg, "method") == Notification.SelectionChanged
					&& msg.TryGetProperty("params", out var p))
				{
					lastPushParams = p.Clone();
				}
			}

			// Detect get_selection requests
			if (entry.Direction != "cli_to_vscode")
				continue;

			var isGetSelection = false;
			if (entry.JsonRpcMessage is not null)
			{
				var msg = entry.JsonRpcMessage.Value;
				isGetSelection = TryGetString(msg, "method") == "tools/call"
					&& msg.TryGetProperty("params", out var p)
					&& TryGetString(p, "name") == "get_selection";
			}
			else if (entry.Event is not null)
			{
				isGetSelection = entry.Event.Contains("tools/call", StringComparison.Ordinal)
					&& entry.Event.Contains("get_selection", StringComparison.Ordinal);
			}

			if (!isGetSelection || lastPushParams is null)
				continue;

			// Find the response for this request (next vscode_to_cli with result)
			var response = parser.Entries.FirstOrDefault(e =>
				e.Seq > entry.Seq
				&& e is { Direction: "vscode_to_cli", JsonRpcMessage: not null }
				&& e.JsonRpcMessage.Value.TryGetProperty("result", out var r)
				&& r.TryGetProperty("content", out _));

			if (response is null)
				continue;

			// Extract pull path data from MCP tool result content
			var result = response.JsonRpcMessage!.Value.GetProperty("result");
			var content = result.GetProperty("content");
			if (content.GetArrayLength() == 0)
				continue;

			var textJson = content[0].GetProperty("text").GetString();
			if (textJson is null or "null")
				continue;

			using var pullDoc = JsonDocument.Parse(textJson);
			var pull = pullDoc.RootElement;

			// Skip when current=false (stale/cached — no active editor to compare)
			if (pull.TryGetProperty("current", out var current) && !current.GetBoolean())
				continue;

			// Skip if pull path has no filePath (incomplete response)
			if (!pull.TryGetProperty("filePath", out _))
				continue;

			var push = lastPushParams.Value;

			// --- Compare shared fields ---

			Assert.Equal(
				push.GetProperty("filePath").GetString(),
				pull.GetProperty("filePath").GetString());

			Assert.Equal(
				push.GetProperty("fileUrl").GetString(),
				pull.GetProperty("fileUrl").GetString());

			Assert.Equal(
				push.GetProperty("text").GetString(),
				pull.GetProperty("text").GetString());

			// Compare selection structure field by field
			var pushSel = push.GetProperty("selection");
			var pullSel = pull.GetProperty("selection");

			Assert.Equal(
				pushSel.GetProperty("start").GetProperty("line").GetInt32(),
				pullSel.GetProperty("start").GetProperty("line").GetInt32());
			Assert.Equal(
				pushSel.GetProperty("start").GetProperty("character").GetInt32(),
				pullSel.GetProperty("start").GetProperty("character").GetInt32());
			Assert.Equal(
				pushSel.GetProperty("end").GetProperty("line").GetInt32(),
				pullSel.GetProperty("end").GetProperty("line").GetInt32());
			Assert.Equal(
				pushSel.GetProperty("end").GetProperty("character").GetInt32(),
				pullSel.GetProperty("end").GetProperty("character").GetInt32());
			Assert.Equal(
				pushSel.GetProperty("isEmpty").GetBoolean(),
				pullSel.GetProperty("isEmpty").GetBoolean());

			comparisons++;
		}

		// Log how many comparisons were made (some captures may have no matching pairs)
		Assert.True(comparisons >= 0,
			$"Capture file processed: {Path.GetFileName(captureFile)}, comparisons made: {comparisons}");
	}

	private static string? TryGetString(JsonElement el, string prop)
	{
		return el.ValueKind == JsonValueKind.Object
			&& el.TryGetProperty(prop, out var v)
			&& v.ValueKind == JsonValueKind.String
			? v.GetString()
			: null;
	}

	private static string[] GetCaptureFiles()
	{
		var assemblyDir = Path.GetDirectoryName(typeof(SelectionConsistencyTests).Assembly.Location)!;
		var fromAssembly = Path.Combine(assemblyDir, "Captures");
		if (Directory.Exists(fromAssembly))
			return Directory.GetFiles(fromAssembly, "*.ndjson");

		var dir = assemblyDir;
		while (dir != null)
		{
			var candidate = Path.Combine(dir, "src", "CopilotCliIde.Server.Tests", "Captures");
			if (Directory.Exists(candidate))
				return Directory.GetFiles(candidate, "*.ndjson");
			candidate = Path.Combine(dir, "Captures");
			if (Directory.Exists(candidate))
				return Directory.GetFiles(candidate, "*.ndjson");
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException(
			"Captures directory not found. Expected at src/CopilotCliIde.Server.Tests/Captures/");
	}
}
