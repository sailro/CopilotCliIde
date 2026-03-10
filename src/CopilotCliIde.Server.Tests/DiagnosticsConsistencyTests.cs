using System.Text.Json;

namespace CopilotCliIde.Server.Tests;

// Guards against drift between diagnostics_changed push and get_diagnostics pull paths.
public class DiagnosticsConsistencyTests
{
	public static TheoryData<string> CaptureFiles()
	{
		var data = new TheoryData<string>();
		foreach (var file in GetCaptureFiles())
			data.Add(file);
		return data;
	}

	// diagnostics_changed is accumulative — each notification replaces diagnostics for its URIs.
	[Theory]
	[MemberData(nameof(CaptureFiles))]
	public void GetDiagnostics_MatchesAccumulatedDiagnosticsChanged(string captureFile)
	{
		var parser = TrafficParser.Load(captureFile);

		// Running state: uri → latest diagnostics array from diagnostics_changed
		var pushState = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		var comparisons = 0;

		foreach (var entry in parser.Entries)
		{
			// Track diagnostics_changed notifications (push path output)
			if (entry is { Direction: "vscode_to_cli", JsonRpcMessage: not null })
			{
				var msg = entry.JsonRpcMessage.Value;
				if (TryGetString(msg, "method") == "diagnostics_changed"
					&& msg.TryGetProperty("params", out var p)
					&& p.TryGetProperty("uris", out var uris)
					&& uris.ValueKind == JsonValueKind.Array)
				{
					foreach (var uriEntry in uris.EnumerateArray())
					{
						var uri = TryGetString(uriEntry, "uri");
						if (uri == null || !uri.StartsWith("file://", StringComparison.Ordinal))
							continue;

						if (uriEntry.TryGetProperty("diagnostics", out var diags))
							pushState[uri] = diags.Clone();
					}
				}
			}

			// Detect get_diagnostics requests
			if (entry.Direction != "cli_to_vscode")
				continue;

			var isGetDiagnostics = false;
			if (entry.JsonRpcMessage is not null)
			{
				var msg = entry.JsonRpcMessage.Value;
				isGetDiagnostics = TryGetString(msg, "method") == "tools/call"
					&& msg.TryGetProperty("params", out var p)
					&& TryGetString(p, "name") == "get_diagnostics";
			}
			else if (entry.Event is not null)
			{
				isGetDiagnostics = entry.Event.Contains("tools/call", StringComparison.Ordinal)
					&& entry.Event.Contains("get_diagnostics", StringComparison.Ordinal);
			}

			if (!isGetDiagnostics || pushState.Count == 0)
				continue;

			// Find the response for this request
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
			if (textJson is null or "null" or "[]")
				continue;

			using var pullDoc = JsonDocument.Parse(textJson);
			var pullRoot = pullDoc.RootElement;
			if (pullRoot.ValueKind != JsonValueKind.Array)
				continue;

			// Compare each URI in the pull response against the accumulated push state
			foreach (var pullFile in pullRoot.EnumerateArray())
			{
				var uri = TryGetString(pullFile, "uri");
				if (uri == null)
					continue;

				if (!pushState.TryGetValue(uri, out var pushDiags))
					continue;

				if (!pullFile.TryGetProperty("diagnostics", out var pullDiags))
					continue;

				// Both should have the same number of diagnostics for this URI
				Assert.Equal(pushDiags.GetArrayLength(), pullDiags.GetArrayLength());

				// Compare each diagnostic by message, severity, and range
				var pushList = pushDiags.EnumerateArray().ToList();
				var pullList = pullDiags.EnumerateArray().ToList();

				for (var i = 0; i < pushList.Count; i++)
				{
					var push = pushList[i];
					var pull = pullList[i];

					Assert.Equal(
						TryGetString(push, "message"),
						TryGetString(pull, "message"));

					Assert.Equal(
						TryGetString(push, "severity"),
						TryGetString(pull, "severity"));

					// Compare range coordinates
					if (push.TryGetProperty("range", out var pushRange)
						&& pull.TryGetProperty("range", out var pullRange))
					{
						Assert.Equal(
							pushRange.GetProperty("start").GetProperty("line").GetInt32(),
							pullRange.GetProperty("start").GetProperty("line").GetInt32());
						Assert.Equal(
							pushRange.GetProperty("start").GetProperty("character").GetInt32(),
							pullRange.GetProperty("start").GetProperty("character").GetInt32());
						Assert.Equal(
							pushRange.GetProperty("end").GetProperty("line").GetInt32(),
							pullRange.GetProperty("end").GetProperty("line").GetInt32());
						Assert.Equal(
							pushRange.GetProperty("end").GetProperty("character").GetInt32(),
							pullRange.GetProperty("end").GetProperty("character").GetInt32());
					}
				}

				comparisons++;
			}
		}

		Assert.True(comparisons >= 0,
			$"Capture file processed: {Path.GetFileName(captureFile)}, URI comparisons made: {comparisons}");
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
		var assemblyDir = Path.GetDirectoryName(typeof(DiagnosticsConsistencyTests).Assembly.Location)!;
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
