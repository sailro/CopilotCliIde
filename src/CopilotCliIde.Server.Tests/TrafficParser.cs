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

	// MCP session ID from HTTP headers — client session on requests, server session on responses.
	// Enables cross-session request→response correlation when JSON-RPC ids collide.
	public string? McpSessionId { get; init; }
}

// Parses NDJSON traffic captures from PipeProxy into structured, queryable data.
// Each line has ts, seq, dir, type, and either "body" (parsed JSON-RPC) or "event" (raw HTTP frames).
public sealed partial class TrafficParser
{
	public IReadOnlyList<TrafficEntry> Entries { get; }

	// Maps client MCP session ID → set of server session IDs observed in handshake responses.
	// Built from non-blocking handshake entries (initialize, notifications, tools/list) using FIFO matching.
	private readonly Dictionary<string, HashSet<string>> _sessionMap = [];

	private TrafficParser(List<TrafficEntry> entries)
	{
		Entries = entries;
	}

	public static TrafficParser Load(string ndjsonPath)
	{
		var entries = new List<TrafficEntry>();
		string? pendingServerSession = null;
		string? pendingClientSession = null;

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

			// Extract MCP session ID from HTTP headers
			if (entry.Event is not null)
			{
				var sessionId = ExtractMcpSessionId(entry.Event);
				if (sessionId is not null)
				{
					entry = entry with { McpSessionId = sessionId };

					// HTTP response → store for propagation to next response body
					if (entry.Direction == "vscode_to_cli" && IsHttpStatusResponse(entry.Event))
						pendingServerSession = sessionId;

					// HTTP request → store for propagation to next request body
					if (entry.Direction == "cli_to_vscode" && IsHttpRequest(entry.Event))
						pendingClientSession = sessionId;
				}
			}

			// Propagate server session from HTTP response header to its body entry.
			// Body entries with JSON-RPC id+result immediately follow their HTTP 200 OK.
			if (entry is { Direction: "vscode_to_cli", McpSessionId: null, JsonRpcMessage: not null }
				&& HasProperty(entry.JsonRpcMessage.Value, "result")
				&& HasProperty(entry.JsonRpcMessage.Value, "id")
				&& pendingServerSession is not null)
			{
				entry = entry with { McpSessionId = pendingServerSession };
				pendingServerSession = null;
			}

			// Propagate client session from HTTP request header to its body entry.
			// Body entries with JSON-RPC method+id immediately follow their HTTP request frame.
			if (entry is { Direction: "cli_to_vscode", McpSessionId: null, JsonRpcMessage: not null }
				&& HasProperty(entry.JsonRpcMessage.Value, "method")
				&& HasProperty(entry.JsonRpcMessage.Value, "id")
				&& pendingClientSession is not null)
			{
				entry = entry with { McpSessionId = pendingClientSession };
				pendingClientSession = null;
			}

			entries.Add(entry);
		}

		var parser = new TrafficParser(entries);
		parser.BuildSessionMap();
		return parser;
	}

	// Builds client→server session mapping from non-blocking handshake entries.
	// The server reuses its session ID after the notification ack, so the notification
	// 202 response's mcp-session-id matches the subsequent tools/call 200 OK.
	private void BuildSessionMap()
	{
		var pendingRequests = new List<(int Seq, string ClientSession)>();

		foreach (var entry in Entries)
		{
			switch (entry)
			{
				case { Direction: "cli_to_vscode", McpSessionId: not null }:
					{
						// Skip tools/call requests — they may block (e.g., open_diff) and
						// their HTTP responses arrive out of FIFO order.
						var isToolsCall = false;
						if (entry.JsonRpcMessage is not null)
							isToolsCall = TryGetStringProperty(entry.JsonRpcMessage.Value, "method") == "tools/call";
						else if (entry.Event is not null)
							isToolsCall = entry.Event.Contains("\"method\":\"tools/call\"", StringComparison.Ordinal);

						if (!isToolsCall)
							pendingRequests.Add((entry.Seq, entry.McpSessionId));
						break;
					}
				case { Direction: "vscode_to_cli", McpSessionId: not null, Event: not null }
					when IsHttpStatusResponse(entry.Event):
					{
						// Match to earliest pending request (FIFO — safe for non-blocking handshake)
						pendingRequests.Sort((a, b) => a.Seq.CompareTo(b.Seq));
						var idx = pendingRequests.FindIndex(r => r.Seq < entry.Seq);
						if (idx < 0)
							break;

						var (_, clientSession) = pendingRequests[idx];
						pendingRequests.RemoveAt(idx);

						if (!_sessionMap.TryGetValue(clientSession, out var serverSessions))
						{
							serverSessions = [];
							_sessionMap[clientSession] = serverSessions;
						}
						serverSessions.Add(entry.McpSessionId);
						break;
					}
			}
		}
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
		var requestEntries = new List<(int Seq, JsonElement? Id, string? ClientSession)>();

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
					requestEntries.Add((entry.Seq, id, entry.McpSessionId));
					continue;
				}
			}

			// Raw text fallback for truncated JSON
			if (entry.Event is null
				|| !entry.Event.Contains("tools/call", StringComparison.Ordinal)
				|| !ContainsToolName(entry.Event, toolName))
				continue;

			var extractedId = TryExtractIdFromText(entry.Event);
			requestEntries.Add((entry.Seq, extractedId, entry.McpSessionId));
		}

		// For each request, find its matching response
		foreach (var (reqSeq, reqId, clientSession) in requestEntries)
		{
			JsonElement? matched = null;

			if (reqId is not null)
			{
				// Resolve valid server sessions for this request's client session
				HashSet<string>? validServerSessions = null;
				if (clientSession is not null)
					_sessionMap.TryGetValue(clientSession, out validServerSessions);

				// Strategy 1: Session-aware ID correlation (when session info available)
				if (validServerSessions is not null)
				{
					var match = Entries.FirstOrDefault(e =>
					e.Seq > reqSeq
					&& e is { Direction: "vscode_to_cli", JsonRpcMessage: not null }
					&& HasProperty(e.JsonRpcMessage.Value, "result")
					&& IsLikelyToolResponse(toolName, e.JsonRpcMessage.Value)
					&& MatchesId(e.JsonRpcMessage.Value, reqId.Value)
					&& e.McpSessionId is not null && validServerSessions.Contains(e.McpSessionId));

					if (match is not null)
						matched = match.JsonRpcMessage;
				}

				// Strategy 2: ID correlation without session (fallback)
				if (matched is null)
				{
					var match = Entries.FirstOrDefault(e =>
					e.Seq > reqSeq
					&& e is { Direction: "vscode_to_cli", JsonRpcMessage: not null }
					&& HasProperty(e.JsonRpcMessage.Value, "result")
					&& IsLikelyToolResponse(toolName, e.JsonRpcMessage.Value)
					&& MatchesId(e.JsonRpcMessage.Value, reqId.Value));

					if (match is not null)
						matched = match.JsonRpcMessage;
				}
			}

			if (matched is null && reqId is null)
			{
				// Strategy 3: Only when no ID is available at all (truly truncated).
				// Requires MCP tool response structure (result.content) to avoid
				// matching initialize or tools/list responses across sessions.
				HashSet<string>? validServerSessions = null;
				if (clientSession is not null)
					_sessionMap.TryGetValue(clientSession, out validServerSessions);

				foreach (var entry in Entries)
				{
					if (entry.Seq <= reqSeq || entry.Direction != "vscode_to_cli")
						continue;

					if (validServerSessions is not null
						&& (entry.McpSessionId is null || !validServerSessions.Contains(entry.McpSessionId)))
					{
						continue;
					}

					var msg = entry.JsonRpcMessage ?? entry.Body;
					if (msg is not null
						&& HasNestedProperty(msg.Value, "result", "content")
						&& IsLikelyToolResponse(toolName, msg.Value))
					{
						matched = msg;
						break;
					}
				}
			}

			if (matched is not null)
				results.Add(matched.Value);
		}

		return results;
	}

	private static bool IsLikelyToolResponse(string toolName, JsonElement response)
	{
		var inner = TryGetInnerToolJson(response);
		if (inner is null)
			return false;

		var root = inner.Value;
		return toolName switch
		{
			"close_diff" => HasProperty(root, "already_closed"),
			"open_diff" => HasProperty(root, "trigger") || HasProperty(root, "result"),
			"get_vscode_info" => HasProperty(root, "appName") && HasProperty(root, "version"),
			"update_session_name" => HasProperty(root, "success")
				&& !HasProperty(root, "appName")
				&& !HasProperty(root, "version")
				&& !HasProperty(root, "already_closed")
				&& !HasProperty(root, "tab_name")
				&& !HasProperty(root, "message")
				&& !HasProperty(root, "result")
				&& !HasProperty(root, "trigger"),
			_ => true
		};
	}

	private static JsonElement? TryGetInnerToolJson(JsonElement response)
	{
		if (!response.TryGetProperty("result", out var result)
			|| !result.TryGetProperty("content", out var content)
			|| content.ValueKind != JsonValueKind.Array
			|| content.GetArrayLength() == 0)
		{
			return null;
		}

		var first = content[0];
		if (!first.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
			return null;

		var text = textEl.GetString();
		if (string.IsNullOrEmpty(text))
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
			&& ContainsToolName(entry.Event, toolName))
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

	[GeneratedRegex(
		"""
		"id"\s*:\s*(\d+)
		""")]
	private static partial Regex IdInTextRegex();

	private static bool ContainsToolName(string text, string toolName)
	{
		var escaped = Regex.Escape(toolName);
		return Regex.IsMatch(text, $"\"name\"\\s*:\\s*\"{escaped}\"", RegexOptions.CultureInvariant);
	}

	// --- MCP session correlation helpers ---

	// Extracts mcp-session-id from HTTP request/response headers (case-insensitive).
	private static string? ExtractMcpSessionId(string httpFrame)
	{
		var match = McpSessionIdRegex().Match(httpFrame);
		return match.Success ? match.Groups[1].Value.Trim() : null;
	}

	private static bool IsHttpStatusResponse(string httpFrame) =>
		httpFrame.StartsWith("HTTP/1.1 ", StringComparison.Ordinal);

	private static bool IsHttpRequest(string httpFrame) =>
		httpFrame.StartsWith("POST /", StringComparison.Ordinal)
		|| httpFrame.StartsWith("GET /", StringComparison.Ordinal)
		|| httpFrame.StartsWith("DELETE /", StringComparison.Ordinal);

	[GeneratedRegex(@"[Mm]cp-[Ss]ession-[Ii]d:\s*([^\r\n]+)", RegexOptions.None)]
	private static partial Regex McpSessionIdRegex();
}
