using System.Text;
using CopilotCliIde.Server;

namespace CopilotCliIde.Server.Tests;

/// <summary>
/// Tests for HTTP response writing — status codes, headers, body encoding.
/// </summary>
public class HttpResponseTests
{
	[Theory]
	[InlineData(200, "OK")]
	[InlineData(202, "Accepted")]
	[InlineData(400, "Bad Request")]
	[InlineData(401, "Unauthorized")]
	[InlineData(404, "Not Found")]
	[InlineData(405, "Method Not Allowed")]
	[InlineData(504, "Gateway Timeout")]
	public async Task WriteHttpResponseAsync_CorrectStatusText(int statusCode, string expectedText)
	{
		using var stream = new MemoryStream();

		await McpPipeServer.WriteHttpResponseAsync(stream, statusCode, "test", CancellationToken.None);

		var response = Encoding.UTF8.GetString(stream.ToArray());
		Assert.StartsWith($"HTTP/1.1 {statusCode} {expectedText}\r\n", response);
	}

	[Fact]
	public async Task WriteHttpResponseAsync_UnknownStatusCode_UsesError()
	{
		using var stream = new MemoryStream();

		await McpPipeServer.WriteHttpResponseAsync(stream, 500, "Internal", CancellationToken.None);

		var response = Encoding.UTF8.GetString(stream.ToArray());
		Assert.StartsWith("HTTP/1.1 500 Error\r\n", response);
	}

	[Fact]
	public async Task WriteHttpResponseAsync_IncludesContentLength()
	{
		using var stream = new MemoryStream();
		var body = "Hello, World!";

		await McpPipeServer.WriteHttpResponseAsync(stream, 200, body, CancellationToken.None);

		var response = Encoding.UTF8.GetString(stream.ToArray());
		Assert.Contains($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n", response);
	}

	[Fact]
	public async Task WriteHttpResponseAsync_DefaultContentType()
	{
		using var stream = new MemoryStream();

		await McpPipeServer.WriteHttpResponseAsync(stream, 200, "", CancellationToken.None);

		var response = Encoding.UTF8.GetString(stream.ToArray());
		Assert.Contains("Content-Type: text/plain\r\n", response);
	}

	[Fact]
	public async Task WriteHttpResponseAsync_CustomContentType()
	{
		using var stream = new MemoryStream();

		await McpPipeServer.WriteHttpResponseAsync(stream, 200, "{}", CancellationToken.None,
			contentType: "text/event-stream");

		var response = Encoding.UTF8.GetString(stream.ToArray());
		Assert.Contains("Content-Type: text/event-stream\r\n", response);
	}

	[Fact]
	public async Task WriteHttpResponseAsync_ExtraHeaders()
	{
		using var stream = new MemoryStream();

		await McpPipeServer.WriteHttpResponseAsync(stream, 200, "", CancellationToken.None,
			extraHeaders: "Mcp-Session-Id: session-42\r\n");

		var response = Encoding.UTF8.GetString(stream.ToArray());
		Assert.Contains("Mcp-Session-Id: session-42\r\n", response);
	}

	[Fact]
	public async Task WriteHttpResponseAsync_KeepAliveHeader()
	{
		using var stream = new MemoryStream();

		await McpPipeServer.WriteHttpResponseAsync(stream, 200, "", CancellationToken.None);

		var response = Encoding.UTF8.GetString(stream.ToArray());
		Assert.Contains("Connection: keep-alive\r\n", response);
	}

	[Fact]
	public async Task WriteHttpResponseAsync_EmptyBody()
	{
		using var stream = new MemoryStream();

		await McpPipeServer.WriteHttpResponseAsync(stream, 202, "", CancellationToken.None);

		var response = Encoding.UTF8.GetString(stream.ToArray());
		Assert.Contains("Content-Length: 0\r\n", response);
	}

	[Fact]
	public async Task WriteHttpResponseAsync_BodyIsAppendedAfterHeaders()
	{
		using var stream = new MemoryStream();
		var body = """{"result":"ok"}""";

		await McpPipeServer.WriteHttpResponseAsync(stream, 200, body, CancellationToken.None);

		var response = Encoding.UTF8.GetString(stream.ToArray());
		// Body comes after the double CRLF that ends headers
		var headerEnd = response.IndexOf("\r\n\r\n");
		Assert.True(headerEnd > 0);
		var responseBody = response[(headerEnd + 4)..];
		Assert.Equal(body, responseBody);
	}

	[Fact]
	public async Task WriteHttpResponseAsync_Utf8Body()
	{
		using var stream = new MemoryStream();
		var body = "naïve café ☕";

		await McpPipeServer.WriteHttpResponseAsync(stream, 200, body, CancellationToken.None);

		var response = Encoding.UTF8.GetString(stream.ToArray());
		var byteCount = Encoding.UTF8.GetByteCount(body);
		Assert.Contains($"Content-Length: {byteCount}\r\n", response);
		Assert.EndsWith(body, response);
	}

	[Fact]
	public async Task WriteHttpResponseAsync_ThenReadBack_RoundTrip()
	{
		// Write a response, then parse it as if we were the client
		using var stream = new MemoryStream();
		var body = """{"jsonrpc":"2.0","result":{"tools":[]},"id":1}""";

		await McpPipeServer.WriteHttpResponseAsync(stream, 200, body, CancellationToken.None,
			contentType: "text/event-stream",
			extraHeaders: "Mcp-Session-Id: test-session\r\n");

		stream.Position = 0;
		var fullResponse = Encoding.UTF8.GetString(stream.ToArray());

		Assert.Contains("HTTP/1.1 200 OK", fullResponse);
		Assert.Contains("text/event-stream", fullResponse);
		Assert.Contains("Mcp-Session-Id: test-session", fullResponse);
		Assert.Contains(body, fullResponse);
	}
}
