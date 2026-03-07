using System.Text.Json;
using System.Text.Json.Serialization;

namespace PipeProxy;

/// <summary>
/// Writes MCP traffic as NDJSON (one JSON object per line).
/// Thread-safe — multiple relay tasks can log concurrently.
/// </summary>
sealed class TrafficLogger : IDisposable
{
	private readonly TextWriter _writer;
	private readonly bool _verbose;
	private int _seq;
	private readonly Lock _writeLock = new();

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public TrafficLogger(TextWriter writer, bool verbose)
	{
		_writer = writer;
		_verbose = verbose;
	}

	public void LogRequest(string direction, string method, string path,
		Dictionary<string, string>? headers, string? body)
	{
		var entry = new LogEntry
		{
			Ts = DateTimeOffset.UtcNow.ToString("o"),
			Seq = Interlocked.Increment(ref _seq),
			Dir = direction,
			Type = "request",
			Http = new HttpInfo { Method = method, Path = path, Headers = headers },
			Body = TryParseBody(body)
		};
		Write(entry);
	}

	public void LogResponse(string direction, int statusCode,
		Dictionary<string, string>? headers, string? body)
	{
		var entry = new LogEntry
		{
			Ts = DateTimeOffset.UtcNow.ToString("o"),
			Seq = Interlocked.Increment(ref _seq),
			Dir = direction,
			Type = "response",
			Http = new HttpInfo { StatusCode = statusCode, Headers = headers },
			Body = TryParseBody(body)
		};
		Write(entry);
	}

	public void LogRawBytes(string direction, string data)
	{
		var entry = new LogEntry
		{
			Ts = DateTimeOffset.UtcNow.ToString("o"),
			Seq = Interlocked.Increment(ref _seq),
			Dir = direction,
			Type = "raw",
			Body = TryParseBody(data)
		};

		// If body couldn't be parsed as JSON, store as raw text
		if (entry.Body == null && !string.IsNullOrEmpty(data))
		{
			entry.Event = data.Length > 500 ? data[..500] + "..." : data;
		}

		Write(entry);
	}

	public void LogSseEvent(string direction, string eventType, string? data)
	{
		var entry = new LogEntry
		{
			Ts = DateTimeOffset.UtcNow.ToString("o"),
			Seq = Interlocked.Increment(ref _seq),
			Dir = direction,
			Type = "sse_event",
			Event = eventType,
			Data = TryParseBody(data)
		};
		Write(entry);
	}

	private void Write(LogEntry entry)
	{
		var json = JsonSerializer.Serialize(entry, SerializerOptions);
		lock (_writeLock)
		{
			_writer.WriteLine(json);
			_writer.Flush();
		}

		if (_verbose)
		{
			var arrow = entry.Dir == "cli_to_vscode" ? "→" : "←";
			var detail = entry.Http?.Method ?? entry.Event ?? "";
			if (entry.Http?.Path != null) detail += $" {entry.Http.Path}";
			if (entry.Http?.StatusCode > 0) detail += $" {entry.Http.StatusCode}";
			Console.Error.WriteLine($"  [{entry.Seq:D4}] {arrow} {entry.Type}: {detail}");
		}
	}

	/// <summary>
	/// Attempts to parse text as JSON. If the text is SSE-formatted (event: / data: lines),
	/// extracts the data payload. Returns null if parsing fails entirely.
	/// </summary>
	private static JsonElement? TryParseBody(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) return null;

		// Try direct JSON parse
		try { return JsonSerializer.Deserialize<JsonElement>(text); }
		catch { /* Not raw JSON */ }

		// Try SSE format — extract data line
		foreach (var line in text.Split('\n'))
		{
			if (line.StartsWith("data: "))
			{
				try { return JsonSerializer.Deserialize<JsonElement>(line[6..]); }
				catch { /* Not valid JSON in data line */ }
			}
		}

		return null;
	}

	public void Dispose()
	{
		_writer.Flush();
		if (_writer != Console.Out)
			_writer.Dispose();
	}
}

sealed class LogEntry
{
	[JsonPropertyName("ts")]
	public string? Ts { get; set; }

	[JsonPropertyName("seq")]
	public int Seq { get; set; }

	[JsonPropertyName("dir")]
	public string? Dir { get; set; }

	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("http")]
	public HttpInfo? Http { get; set; }

	[JsonPropertyName("body")]
	public JsonElement? Body { get; set; }

	[JsonPropertyName("event")]
	public string? Event { get; set; }

	[JsonPropertyName("data")]
	public JsonElement? Data { get; set; }
}

sealed class HttpInfo
{
	[JsonPropertyName("method")]
	public string? Method { get; set; }

	[JsonPropertyName("path")]
	public string? Path { get; set; }

	[JsonPropertyName("statusCode")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public int StatusCode { get; set; }

	[JsonPropertyName("headers")]
	public Dictionary<string, string>? Headers { get; set; }
}
