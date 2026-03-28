using System.Net;
using System.Text;

namespace CopilotCliIde.Server;

internal static class HttpPipeFraming
{
	private const string Crlf = "\r\n";
	private const string HeaderTerminator = "\r\n\r\n";
	private const string ContentLengthHeader = "content-length";
	private const string ContentTypeHeader = "content-type";
	private const string ConnectionHeader = "connection";
	private const string TransferEncodingHeader = "transfer-encoding";
	private const string CacheControlHeader = "cache-control";
	private const string EventStreamContentType = "text/event-stream";

	private static readonly byte[] _chunkEndBytes = Encoding.UTF8.GetBytes($"{Crlf}0{HeaderTerminator}");
	private static readonly byte[] _chunkTerminatorBytes = Encoding.UTF8.GetBytes($"0{HeaderTerminator}");

	public static async Task<(string? method, string? path, Dictionary<string, string> headers, string body)> ReadHttpRequestAsync(Stream stream, CancellationToken ct)
	{
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var sb = new StringBuilder();
		var buffer = new byte[1];
		var headerComplete = false;

		// Read headers byte by byte until \r\n\r\n
		while (!headerComplete && !ct.IsCancellationRequested)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
			if (read == 0) return (null, null, headers, "");

			sb.Append((char)buffer[0]);
			if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == HeaderTerminator)
				headerComplete = true;
		}

		var headerText = sb.ToString();
		var lines = headerText.Split([Crlf], StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length == 0) return (null, null, headers, "");

		// Parse request line
		var parts = lines[0].Split(' ');
		var method = parts.Length > 0 ? parts[0] : null;
		var path = parts.Length > 1 ? parts[1] : null;

		// Parse headers
		for (int i = 1; i < lines.Length; i++)
		{
			var colonIdx = lines[i].IndexOf(':');
			if (colonIdx <= 0)
				continue;

			var key = lines[i][..colonIdx].Trim();
			var value = lines[i][(colonIdx + 1)..].Trim();
			headers[key] = value;
		}

		// Read body
		var body = "";
		if (headers.TryGetValue(ContentLengthHeader, out var clStr) && int.TryParse(clStr, out var contentLength) && contentLength > 0)
		{
			var bodyBuffer = new byte[contentLength];
			var totalRead = 0;
			while (totalRead < contentLength)
			{
				var read = await stream.ReadAsync(bodyBuffer.AsMemory(totalRead, contentLength - totalRead), ct);
				if (read == 0) break;
				totalRead += read;
			}
			body = Encoding.UTF8.GetString(bodyBuffer, 0, totalRead);
		}
		else if (headers.TryGetValue(TransferEncodingHeader, out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
		{
			// Read chunked transfer encoding
			body = await ReadChunkedBodyAsync(stream, ct);
		}

		return (method, path, headers, body);
	}

	public static async Task<string> ReadChunkedBodyAsync(Stream stream, CancellationToken ct)
	{
		var result = new StringBuilder();
		var lineBuf = new StringBuilder();

		while (true)
		{
			// Read chunk size line (hex\r\n)
			lineBuf.Clear();
			while (true)
			{
				var b = new byte[1];
				var read = await stream.ReadAsync(b.AsMemory(0, 1), ct);
				if (read == 0) return result.ToString();
				lineBuf.Append((char)b[0]);
				if (lineBuf is [.., '\r', '\n'])
					break;
			}

			var sizeLine = lineBuf.ToString().TrimEnd('\r', '\n').Trim();
			// Strip chunk extensions if any
			var semiIdx = sizeLine.IndexOf(';');
			if (semiIdx >= 0) sizeLine = sizeLine[..semiIdx];

			if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, null, out var chunkSize) || chunkSize == 0)
				break;

			// Read chunk data
			var chunkBuf = new byte[chunkSize];
			var totalRead = 0;
			while (totalRead < chunkSize)
			{
				var read = await stream.ReadAsync(chunkBuf.AsMemory(totalRead, chunkSize - totalRead), ct);
				if (read == 0) break;
				totalRead += read;
			}
			result.Append(Encoding.UTF8.GetString(chunkBuf, 0, totalRead));

			// Read trailing \r\n after chunk data
			await ReadTrailingCrlfAsync(stream, ct);
		}

		// Read trailing headers/\r\n after the final 0-size chunk
		await ReadTrailingCrlfAsync(stream, ct);

		return result.ToString();
	}

	public static async Task WriteHttpResponseAsync(Stream stream, int statusCode, string body, CancellationToken ct,
		string contentType = "text/plain", string extraHeaders = "")
	{
		// Use HttpResponseMessage to format the status line properly
		using var response = new HttpResponseMessage((HttpStatusCode)statusCode);
		var statusText = response.ReasonPhrase ?? "Unknown";

		var bodyBytes = Encoding.UTF8.GetBytes(body);
		var useChunked = contentType == EventStreamContentType;

		// Build status line and headers using proper HTTP formatting
		var sb = new StringBuilder();
		sb.Append($"HTTP/1.1 {statusCode} {statusText}{Crlf}");
		sb.Append($"{ContentTypeHeader}: {contentType}{Crlf}");
		if (useChunked)
		{
			sb.Append($"{CacheControlHeader}: no-cache{Crlf}");
			sb.Append($"{TransferEncodingHeader}: chunked{Crlf}");
		}
		else
			sb.Append($"{ContentLengthHeader}: {bodyBytes.Length}{Crlf}");
		sb.Append($"{ConnectionHeader}: keep-alive{Crlf}");
		if (!string.IsNullOrEmpty(extraHeaders))
			sb.Append(extraHeaders);
		sb.Append(Crlf);

		var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
		await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), ct);

		switch (useChunked)
		{
			case true when bodyBytes.Length > 0:
				{
					var chunk = Encoding.UTF8.GetBytes($"{bodyBytes.Length:x}{Crlf}");
					var fullChunk = new byte[chunk.Length + bodyBytes.Length + _chunkEndBytes.Length];
					Buffer.BlockCopy(chunk, 0, fullChunk, 0, chunk.Length);
					Buffer.BlockCopy(bodyBytes, 0, fullChunk, chunk.Length, bodyBytes.Length);
					Buffer.BlockCopy(_chunkEndBytes, 0, fullChunk, chunk.Length + bodyBytes.Length, _chunkEndBytes.Length);
					await stream.WriteAsync(fullChunk.AsMemory(0, fullChunk.Length), ct);
					break;
				}
			case true:
				{
					await stream.WriteAsync(_chunkTerminatorBytes.AsMemory(0, _chunkTerminatorBytes.Length), ct);
					break;
				}
			default:
				await stream.WriteAsync(bodyBytes.AsMemory(0, bodyBytes.Length), ct);
				break;
		}

		await stream.FlushAsync(ct);
	}

	private static async Task ReadTrailingCrlfAsync(Stream stream, CancellationToken ct)
	{
		var buf = new byte[2];
		await stream.ReadExactlyAsync(buf.AsMemory(0, 2), ct);
	}
}
