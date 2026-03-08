using System.Text.Json;

namespace CopilotCliIde.Server.Tests;

/// <summary>
/// Tests that the JSON-RPC notification format matches what Copilot CLI expects.
/// The format is: {"jsonrpc":"2.0","method":"...","params":{...}}
/// Wrapped in SSE: event: message\ndata: {json}\n\n
/// </summary>
public class NotificationFormatTests
{
	[Fact]
	public void SelectionChangedNotification_MatchesExpectedFormat()
	{
		// This mirrors what Program.cs does when forwarding selection changes
		var notification = new
		{
			text = "var x = 42;",
			filePath = @"C:\src\Program.cs",
			fileUrl = "file:///C:/src/Program.cs",
			selection = new
			{
				start = new { line = 5, character = 0 },
				end = new { line = 5, character = 11 },
				isEmpty = false
			}
		};

		var jsonRpc = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			method = "selection_changed",
			@params = notification
		});

		var doc = JsonDocument.Parse(jsonRpc);
		var root = doc.RootElement;

		Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
		Assert.Equal("selection_changed", root.GetProperty("method").GetString());

		var p = root.GetProperty("params");
		Assert.Equal("var x = 42;", p.GetProperty("text").GetString());
		Assert.Equal(@"C:\src\Program.cs", p.GetProperty("filePath").GetString());

		var sel = p.GetProperty("selection");
		Assert.Equal(5, sel.GetProperty("start").GetProperty("line").GetInt32());
		Assert.Equal(0, sel.GetProperty("start").GetProperty("character").GetInt32());
		Assert.Equal(11, sel.GetProperty("end").GetProperty("character").GetInt32());
		Assert.False(sel.GetProperty("isEmpty").GetBoolean());
	}

	[Fact]
	public void SelectionChangedNotification_NullSelection()
	{
		var notification = new
		{
			text = "",
			filePath = (string?)null,
			fileUrl = (string?)null,
			selection = (object?)null
		};

		var jsonRpc = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			method = "selection_changed",
			@params = notification
		});

		var doc = JsonDocument.Parse(jsonRpc);
		var p = doc.RootElement.GetProperty("params");

		Assert.Equal("", p.GetProperty("text").GetString());
		Assert.Equal(JsonValueKind.Null, p.GetProperty("selection").ValueKind);
	}

	[Fact]
	public void SseEventFormat_IsCorrect()
	{
		// Verify the SSE event wrapping format
		var notification = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "test", @params = new { } });
		var sseEvent = $"event: message\ndata: {notification}\n\n";

		Assert.StartsWith("event: message\n", sseEvent);
		Assert.Contains("data: ", sseEvent);
		Assert.EndsWith("\n\n", sseEvent);

		// Extract the JSON from the SSE event
		var dataLine = sseEvent.Split('\n').First(l => l.StartsWith("data: "));
		var json = dataLine["data: ".Length..];
		var doc = JsonDocument.Parse(json);
		Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
	}

	[Fact]
	public void SelectionNotification_EmptySelection_UsesDefaults()
	{
		// When selection is null, the notification uses defaults of 0
		var notification = new
		{
			text = "",
			filePath = @"C:\test.cs",
			fileUrl = (string?)null,
			selection = new
			{
				start = new { line = 0, character = 0 },
				end = new { line = 0, character = 0 },
				isEmpty = true
			}
		};

		var json = JsonSerializer.Serialize(notification);
		var doc = JsonDocument.Parse(json);
		var sel = doc.RootElement.GetProperty("selection");

		Assert.True(sel.GetProperty("isEmpty").GetBoolean());
		Assert.Equal(0, sel.GetProperty("start").GetProperty("line").GetInt32());
	}

	[Fact]
	public void DiagnosticsChangedNotification_MatchesExpectedFormat()
	{
		var notification = new
		{
			uris = new[]
			{
				new
				{
					uri = "file:///C:/src/Program.cs",
					diagnostics = new[]
					{
						new
						{
							message = "CS0103: The name 'x' does not exist",
							severity = "error",
							range = new
							{
								start = new { line = 41, character = 7 },
								end = new { line = 41, character = 8 }
							},
							source = "WebApp",
							code = "CS0103"
						}
					}
				}
			}
		};

		var jsonRpc = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			method = "diagnostics_changed",
			@params = notification
		});

		var doc = JsonDocument.Parse(jsonRpc);
		var root = doc.RootElement;

		Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
		Assert.Equal("diagnostics_changed", root.GetProperty("method").GetString());

		var p = root.GetProperty("params");
		var uris = p.GetProperty("uris");
		Assert.Equal(1, uris.GetArrayLength());

		var firstUri = uris[0];
		Assert.Equal("file:///C:/src/Program.cs", firstUri.GetProperty("uri").GetString());

		var diag = firstUri.GetProperty("diagnostics")[0];
		Assert.Equal("error", diag.GetProperty("severity").GetString());
		Assert.Equal("CS0103", diag.GetProperty("code").GetString());
		Assert.Equal("WebApp", diag.GetProperty("source").GetString());

		var range = diag.GetProperty("range");
		Assert.Equal(41, range.GetProperty("start").GetProperty("line").GetInt32());
		Assert.Equal(8, range.GetProperty("end").GetProperty("character").GetInt32());
	}

	[Fact]
	public void DiagnosticsChangedNotification_EmptyUris()
	{
		var jsonRpc = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			method = "diagnostics_changed",
			@params = new { uris = Array.Empty<object>() }
		});

		var doc = JsonDocument.Parse(jsonRpc);
		var uris = doc.RootElement.GetProperty("params").GetProperty("uris");
		Assert.Equal(0, uris.GetArrayLength());
	}
}
