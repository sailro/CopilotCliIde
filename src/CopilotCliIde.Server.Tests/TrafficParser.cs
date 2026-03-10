using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotCliIde.Server.Tests;

public sealed record TrafficEntry
{
	public DateTimeOffset Timestamp { get; init; }
	public int Seq { get; init; }
	public required string Direction { get; init; }
	public required string Type { get; init; }

	// Already-parsed JSON body (when raw data was valid JSON)
	public JsonElement? Body { get; init; }

	// Raw HTTP frame text (when body couldn't be parsed)
	public string? Event { get; init; }

	// Extracted JSON-RPC message — from body directly, or parsed out of the HTTP frame
	public JsonElement? JsonRpcMessage { get; init; }
}

// Parses NDJSON traffic captures from PipeProxy into structured, queryable data.
// Each line has ts, seq, dir, type, and either "body" (parsed JSON-RPC) or "event" (raw HTTP frames).
public sealed partial class TrafficParser
{
	public IReadOnlyList<TrafficEntry> Entries { get; }

	private TrafficParser(List<TrafficEntry> entries)
	{
		Entries = entries;
	}

	public static TrafficParser Load(string ndjsonPath)
	{
		var entries = new List<TrafficEntry>();

		foreach (var rawLine in File.ReadLines(ndjsonPath))
		{
			var line = rawLine.Trim();
			if (line.Length == 0)
				continue;

			// Strip BOM if present
			if (line[0] == '\uFEFF')
				line = line[1..];
			if (line.Length == 0)
				continue;

			using var doc = JsonDocument.Parse(line);
			var root = doc.RootElement;

			var entry = new TrafficEntry
			{
				Timestamp = root.GetProperty("ts").GetDateTimeOffset(),
				Seq = root.GetProperty("seq").GetInt32(),
				Direction = root.GetProperty("dir").GetString()!,
				Type = root.GetProperty("type").GetString()!
			};

			if (root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.Object)
			{
				var cloned = bodyEl.Clone();
				entry = entry with { Body = cloned };

				// Only treat as JSON-RPC message if it has recognizable fields;
				// empty bodies ({}) from truncated captures should fall through
				// to event extraction below.
				if (HasProperty(cloned, "jsonrpc") || HasProperty(cloned, "method")
					|| HasProperty(cloned, "id") || HasProperty(cloned, "result") || HasProperty(cloned, "error"))
				{
					entry = entry with { JsonRpcMessage = cloned };
				}
			}

			if (root.TryGetProperty("event", out var eventEl))
			{
				var raw = eventEl.GetString();
				entry = entry with { Event = raw };

				// Only try HTTP extraction if we don't already have a JSON-RPC message from body
				if (raw is not null && entry.JsonRpcMessage is null)
				{
					var extracted = ExtractJsonFromHttpFrame(raw);
					if (extracted is not null)
						entry = entry with { JsonRpcMessage = extracted };
				}
			}

			entries.Add(entry);
		}

		return new TrafficParser(entries);
	}

	public JsonElement? GetInitializeResponse()
	{
		return Entries
		.Where(e => e is { Direction: "vscode_to_cli", JsonRpcMessage: not null }
					&& HasProperty(e.JsonRpcMessage.Value, "result")
					&& HasNestedProperty(e.JsonRpcMessage.Value, "result", "protocolVersion"))
		.Select(e => e.JsonRpcMessage)
		.FirstOrDefault();
	}

	public JsonElement? GetToolsListResponse()
	{
		return Entries
		.Where(e => e is { Direction: "vscode_to_cli", JsonRpcMessage: not null }
					&& HasProperty(e.JsonRpcMessage.Value, "result")
					&& HasNestedProperty(e.JsonRpcMessage.Value, "result", "tools"))
		.Select(e => e.JsonRpcMessage)
		.FirstOrDefault();
	}

	// Uses JSON-RPC id correlation; falls back to sequence-based when request JSON is truncated.
	public JsonElement? GetToolCallResponse(string toolName)
	{
		// Strategy 1: Correlate via JSON-RPC id from a fully-parsed request
		var request = FindToolCallRequest(toolName);
		if (request is not null)
		{
			var (requestId, requestSeq) = request.Value;
			var match = Entries.FirstOrDefault(e =>
			e.Seq > requestSeq
			&& e is { Direction: "vscode_to_cli", JsonRpcMessage: not null }
			&& HasProperty(e.JsonRpcMessage.Value, "result")
			&& MatchesId(e.JsonRpcMessage.Value, requestId));

			if (match is not null)
				return match.JsonRpcMessage;
		}

		// Strategy 2: Find the request event containing the tool name in raw text,
		// then the next vscode_to_cli body with a result (tool call response pattern)
		var fallbackSeq = FindToolCallRequestSeq(toolName);
		if (fallbackSeq is null)
			return null;

		var fallbackMatch = Entries.FirstOrDefault(e =>
		e.Seq > fallbackSeq.Value
		&& e is { Direction: "vscode_to_cli", Body: not null }
		&& HasProperty(e.Body.Value, "result"));

		if (fallbackMatch is not null)
			return fallbackMatch.JsonRpcMessage ?? fallbackMatch.Body;

		return null;
	}

	public List<JsonElement> GetAllToolCallResponses(string toolName)
	{
		var results = new List<JsonElement>();

		// Collect all requests for this tool (both parsed and raw-text matches)
		var requestEntries = new List<(int Seq, JsonElement? Id)>();

		foreach (var entry in Entries)
		{
			if (entry.Direction != "cli_to_vscode")
				continue;

			// Fully-parsed request
			if (entry.JsonRpcMessage is not null)
			{
				var msg = entry.JsonRpcMessage.Value;
				if (TryGetStringProperty(msg, "method") == "tools/call"
				&& HasNestedStringValue(msg, "params", "name", toolName))
				{
					var id = msg.TryGetProperty("id", out var idEl) ? (JsonElement?)idEl.Clone() : null;
					requestEntries.Add((entry.Seq, id));
					continue;
				}
			}

			// Raw text fallback for truncated JSON
			var namePattern = $"\"name\":\"{toolName}\"";
			if (entry.Event is not null
			&& entry.Event.Contains("tools/call", StringComparison.Ordinal)
			&& entry.Event.Contains(namePattern, StringComparison.Ordinal))
			{
				var extractedId = TryExtractIdFromText(entry.Event);
				requestEntries.Add((entry.Seq, extractedId));
			}
		}

		// For each request, find its matching response
		foreach (var (reqSeq, reqId) in requestEntries)
		{
			JsonElement? matched = null;

			if (reqId is not null)
			{
				// Strategy 1: ID correlation scoped after the request
				var match = Entries.FirstOrDefault(e =>
				e.Seq > reqSeq
				&& e is { Direction: "vscode_to_cli", JsonRpcMessage: not null }
				&& HasProperty(e.JsonRpcMessage.Value, "result")
				&& MatchesId(e.JsonRpcMessage.Value, reqId.Value));

				if (match is not null)
					matched = match.JsonRpcMessage;
			}

			if (matched is null && reqId is null)
			{
				// Strategy 2: Only when no ID is available at all (truly truncated).
				// Requires MCP tool response structure (result.content) to avoid
				// matching initialize or tools/list responses across sessions.
				var match = Entries.FirstOrDefault(e =>
				e.Seq > reqSeq
				&& e is { Direction: "vscode_to_cli", Body: not null }
				&& HasNestedProperty(e.Body.Value, "result", "content"));

				if (match is not null)
					matched = match.JsonRpcMessage ?? match.Body;
			}

			if (matched is not null)
				results.Add(matched.Value);
		}

		return results;
	}

	public List<JsonElement> GetNotifications(string method)
	{
		return [.. Entries
		.Where(e => e is { Direction: "vscode_to_cli", JsonRpcMessage: not null }
					&& TryGetStringProperty(e.JsonRpcMessage.Value, "method") == method)
		.Select(e => e.JsonRpcMessage!.Value)];
	}

	// --- HTTP frame extraction ---

	// Extracts JSON-RPC body from raw HTTP frame; handles chunked encoding, SSE, and plain JSON.
	internal static JsonElement? ExtractJsonFromHttpFrame(string httpFrame)
	{
		// Split headers from body on double CRLF
		var headerEnd = httpFrame.IndexOf("\r\n\r\n", StringComparison.Ordinal);
		if (headerEnd < 0)
			return null;

		var bodyPart = httpFrame[(headerEnd + 4)..];
		if (bodyPart.Length == 0)
			return null;

		// Try SSE format: "event: message\ndata: {json}"
		// Skip chunked encoding markers and brace-match to extract JSON
		return ExtractFromSse(bodyPart) ?? ExtractJsonWithBraceMatching(bodyPart);
	}

	private static JsonElement? ExtractFromSse(string text)
	{
		var match = SseDataRegex().Match(text);
		return !match.Success ? null : TryParseJson(match.Groups[1].Value.Trim());
	}

	// Brace-matches to extract a complete JSON object; returns null if truncated.
	private static JsonElement? ExtractJsonWithBraceMatching(string text)
	{
		var jsonStart = text.IndexOf('{');
		if (jsonStart < 0)
			return null;

		var depth = 0;
		var inString = false;
		var escape = false;

		for (var i = jsonStart; i < text.Length; i++)
		{
			var c = text[i];
			if (escape) { escape = false; continue; }
			switch (c)
			{
				case '\\' when inString:
					escape = true;
					continue;
				case '"':
					inString = !inString;
					continue;
				case '{' when !inString:
					depth++;
					break;
				case '}' when !inString:
					depth--;
					if (depth == 0)
					{
						var jsonStr = text[jsonStart..(i + 1)];
						return TryParseJson(jsonStr);
					}
					break;
			}
		}

		// Truncated JSON — brace never closed
		return null;
	}

	private static JsonElement? TryParseJson(string text)
	{
		text = text.Trim();
		if (text.Length == 0 || text[0] is not ('{' or '['))
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

	// --- Tool call correlation helpers ---

	private (JsonElement Id, int Seq)? FindToolCallRequest(string toolName)
	{
		foreach (var entry in Entries)
		{
			if (entry.Direction != "cli_to_vscode" || entry.JsonRpcMessage is null)
				continue;

			var msg = entry.JsonRpcMessage.Value;
			if (TryGetStringProperty(msg, "method") == "tools/call"
			&& HasNestedStringValue(msg, "params", "name", toolName)
			&& msg.TryGetProperty("id", out var idEl))
			{
				return (idEl.Clone(), entry.Seq);
			}
		}

		return null;
	}

	// Fallback for when request JSON is truncated and can't be fully parsed.
	private int? FindToolCallRequestSeq(string toolName)
	{
		var namePattern = $"\"name\":\"{toolName}\"";

		foreach (var entry in Entries)
		{
			if (entry.Direction != "cli_to_vscode")
				continue;

			// Check extracted JSON-RPC first
			if (entry.JsonRpcMessage is not null
			&& TryGetStringProperty(entry.JsonRpcMessage.Value, "method") == "tools/call"
			&& HasNestedStringValue(entry.JsonRpcMessage.Value, "params", "name", toolName))
			{
				return entry.Seq;
			}

			// Fall back to raw text search in event (handles truncated JSON)
			if (entry.Event is not null
			&& entry.Event.Contains("tools/call", StringComparison.Ordinal)
			&& entry.Event.Contains(namePattern, StringComparison.Ordinal))
			{
				return entry.Seq;
			}
		}

		return null;
	}

	// --- JSON element helpers ---

	private static bool HasProperty(JsonElement element, string name) =>
	element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out _);

	private static bool HasNestedProperty(JsonElement element, string parent, string child) =>
	element.ValueKind == JsonValueKind.Object
	&& element.TryGetProperty(parent, out var parentEl)
	&& parentEl.ValueKind == JsonValueKind.Object
	&& parentEl.TryGetProperty(child, out _);

	private static bool HasNestedStringValue(JsonElement element, string parent, string child, string value) =>
	element.ValueKind == JsonValueKind.Object
	&& element.TryGetProperty(parent, out var parentEl)
	&& parentEl.ValueKind == JsonValueKind.Object
	&& parentEl.TryGetProperty(child, out var childEl)
	&& childEl.ValueKind == JsonValueKind.String
	&& childEl.GetString() == value;

	private static string? TryGetStringProperty(JsonElement element, string name)
	{
		if (element.ValueKind == JsonValueKind.Object
		&& element.TryGetProperty(name, out var prop)
		&& prop.ValueKind == JsonValueKind.String)
		{
			return prop.GetString();
		}

		return null;
	}

	private static bool MatchesId(JsonElement element, JsonElement id)
	{
		if (!element.TryGetProperty("id", out var elId))
			return false;

		return id.ValueKind switch
		{
			JsonValueKind.Number => elId.ValueKind == JsonValueKind.Number
			&& id.GetInt64() == elId.GetInt64(),
			JsonValueKind.String => elId.ValueKind == JsonValueKind.String
			&& id.GetString() == elId.GetString(),
			_ => false
		};
	}

	[GeneratedRegex(@"data:\s*(\{.+\})", RegexOptions.Singleline)]
	private static partial Regex SseDataRegex();

	// Extracts JSON-RPC "id" via regex — works even when the full JSON is truncated.
	private static JsonElement? TryExtractIdFromText(string text)
	{
		var match = IdInTextRegex().Match(text);
		if (!match.Success)
			return null;

		try
		{
			using var doc = JsonDocument.Parse(match.Groups[1].Value);
			return doc.RootElement.Clone();
		}
		catch
		{
			return null;
		}
	}

	[GeneratedRegex(@"""id""\s*:\s*(\d+)")]
	private static partial Regex IdInTextRegex();
}
