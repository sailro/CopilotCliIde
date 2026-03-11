using System.Text;

namespace CopilotCliIde.Server.Tests;

public class HttpParsingTests
{
	[Fact]
	public async Task ReadHttpRequestAsync_ParsesGetRequest()
	{
		const string request = "GET /mcp HTTP/1.1\r\nHost: localhost\r\nAuthorization: Nonce abc123\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));

		var (method, path, headers, body) = await McpPipeServer.ReadHttpRequestAsync(stream, CancellationToken.None);

		Assert.Equal("GET", method);
		Assert.Equal("/mcp", path);
		Assert.Equal("localhost", headers["Host"]);
		Assert.Equal("Nonce abc123", headers["Authorization"]);
		Assert.Equal("", body);
	}

	[Fact]
	public async Task ReadHttpRequestAsync_ParsesPostWithBody()
	{
		const string jsonBody = """{"jsonrpc":"2.0","method":"tools/call","id":1}""";
		var request = $"POST /mcp HTTP/1.1\r\nContent-Length: {jsonBody.Length}\r\nContent-Type: application/json\r\n\r\n{jsonBody}";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));

		var (method, path, headers, body) = await McpPipeServer.ReadHttpRequestAsync(stream, CancellationToken.None);

		Assert.Equal("POST", method);
		Assert.Equal("/mcp", path);
		Assert.Equal("application/json", headers["Content-Type"]);
		Assert.Equal(jsonBody, body);
	}

	[Fact]
	public async Task ReadHttpRequestAsync_HeadersAreCaseInsensitive()
	{
		const string request = "GET / HTTP/1.1\r\ncontent-type: text/plain\r\nAUTHORIZATION: Nonce xyz\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));

		var (_, _, headers, _) = await McpPipeServer.ReadHttpRequestAsync(stream, CancellationToken.None);

		Assert.Equal("text/plain", headers["Content-Type"]);
		Assert.Equal("Nonce xyz", headers["authorization"]);
	}

	[Fact]
	public async Task ReadHttpRequestAsync_EmptyStream_ReturnsNulls()
	{
		using var stream = new MemoryStream([]);

		var (method, path, _, body) = await McpPipeServer.ReadHttpRequestAsync(stream, CancellationToken.None);

		Assert.Null(method);
		Assert.Null(path);
		Assert.Equal("", body);
	}

	[Fact]
	public async Task ReadHttpRequestAsync_ZeroContentLength_ReturnsEmptyBody()
	{
		const string request = "POST / HTTP/1.1\r\nContent-Length: 0\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));

		var (method, path, _, body) = await McpPipeServer.ReadHttpRequestAsync(stream, CancellationToken.None);

		Assert.Equal("POST", method);
		Assert.Equal("/", path);
		Assert.Equal("", body);
	}

	[Fact]
	public async Task ReadHttpRequestAsync_DeleteMethod()
	{
		const string request = "DELETE /mcp HTTP/1.1\r\nAuthorization: Nonce test\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));

		var (method, path, _, _) = await McpPipeServer.ReadHttpRequestAsync(stream, CancellationToken.None);

		Assert.Equal("DELETE", method);
		Assert.Equal("/mcp", path);
	}

	[Fact]
	public async Task ReadHttpRequestAsync_ChunkedTransferEncoding()
	{
		// "Hello" = 5 bytes, "World" = 5 bytes
		const string request = "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nHello\r\n5\r\nWorld\r\n0\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));

		var (method, _, _, body) = await McpPipeServer.ReadHttpRequestAsync(stream, CancellationToken.None);

		Assert.Equal("POST", method);
		Assert.Equal("HelloWorld", body);
	}

	[Fact]
	public async Task ReadHttpRequestAsync_LargeBody()
	{
		var largePayload = new string('x', 10_000);
		var request = $"POST / HTTP/1.1\r\nContent-Length: {largePayload.Length}\r\n\r\n{largePayload}";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));

		var (method, _, _, body) = await McpPipeServer.ReadHttpRequestAsync(stream, CancellationToken.None);

		Assert.Equal("POST", method);
		Assert.Equal(10_000, body.Length);
		Assert.Equal(largePayload, body);
	}

	[Fact]
	public async Task ReadHttpRequestAsync_MultipleHeaders()
	{
		const string request = "GET / HTTP/1.1\r\nHost: localhost\r\nAccept: */*\r\nX-Custom: foo\r\nMcp-Session-Id: session-123\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));

		var (_, _, headers, _) = await McpPipeServer.ReadHttpRequestAsync(stream, CancellationToken.None);

		Assert.Equal(4, headers.Count);
		Assert.Equal("session-123", headers["Mcp-Session-Id"]);
		Assert.Equal("foo", headers["X-Custom"]);
	}

	[Fact]
	public async Task ReadHttpRequestAsync_HeaderWithColonInValue()
	{
		const string request = "GET / HTTP/1.1\r\nAuthorization: Bearer abc:def:ghi\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));

		var (_, _, headers, _) = await McpPipeServer.ReadHttpRequestAsync(stream, CancellationToken.None);

		Assert.Equal("Bearer abc:def:ghi", headers["Authorization"]);
	}

	[Fact]
	public async Task ReadHttpRequestAsync_Cancellation()
	{
		// Create a stream that will block indefinitely (pipe that never gets data)
		var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In);
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => McpPipeServer.ReadHttpRequestAsync(pipe, cts.Token));
	}
}
