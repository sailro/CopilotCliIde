using System.Text;

namespace CopilotCliIde.Server.Tests;

/// <summary>
/// Tests for the chunked transfer-encoding parser.
/// This is critical — Copilot CLI sends chunked bodies over the pipe.
/// </summary>
public class ChunkedEncodingTests
{
	[Fact]
	public async Task ReadChunkedBodyAsync_SingleChunk()
	{
		const string chunked = "D\r\nHello, World!\r\n0\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(chunked));

		var body = await McpPipeServer.ReadChunkedBodyAsync(stream, CancellationToken.None);

		Assert.Equal("Hello, World!", body);
	}

	[Fact]
	public async Task ReadChunkedBodyAsync_MultipleChunks()
	{
		const string chunked = "5\r\nHello\r\n1\r\n \r\n5\r\nWorld\r\n0\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(chunked));

		var body = await McpPipeServer.ReadChunkedBodyAsync(stream, CancellationToken.None);

		Assert.Equal("Hello World", body);
	}

	[Fact]
	public async Task ReadChunkedBodyAsync_EmptyBody()
	{
		const string chunked = "0\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(chunked));

		var body = await McpPipeServer.ReadChunkedBodyAsync(stream, CancellationToken.None);

		Assert.Equal("", body);
	}

	[Fact]
	public async Task ReadChunkedBodyAsync_LargeChunk()
	{
		var data = new string('A', 4096);
		var hexSize = data.Length.ToString("x");
		var chunked = $"{hexSize}\r\n{data}\r\n0\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(chunked));

		var body = await McpPipeServer.ReadChunkedBodyAsync(stream, CancellationToken.None);

		Assert.Equal(4096, body.Length);
		Assert.Equal(data, body);
	}

	[Fact]
	public async Task ReadChunkedBodyAsync_UppercaseHex()
	{
		const string chunked = "A\r\n0123456789\r\n0\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(chunked));

		var body = await McpPipeServer.ReadChunkedBodyAsync(stream, CancellationToken.None);

		Assert.Equal("0123456789", body);
	}

	[Fact]
	public async Task ReadChunkedBodyAsync_LowercaseHex()
	{
		const string chunked = "a\r\n0123456789\r\n0\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(chunked));

		var body = await McpPipeServer.ReadChunkedBodyAsync(stream, CancellationToken.None);

		Assert.Equal("0123456789", body);
	}

	[Fact]
	public async Task ReadChunkedBodyAsync_ChunkExtensions()
	{
		// Chunk extensions (;key=value) should be stripped
		const string chunked = "5;ext=val\r\nHello\r\n0\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(chunked));

		var body = await McpPipeServer.ReadChunkedBodyAsync(stream, CancellationToken.None);

		Assert.Equal("Hello", body);
	}

	[Fact]
	public async Task ReadChunkedBodyAsync_JsonPayload()
	{
		const string json = """{"jsonrpc":"2.0","method":"tools/list","id":1}""";
		var hexSize = Encoding.UTF8.GetByteCount(json).ToString("x");
		var chunked = $"{hexSize}\r\n{json}\r\n0\r\n\r\n";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(chunked));

		var body = await McpPipeServer.ReadChunkedBodyAsync(stream, CancellationToken.None);

		Assert.Equal(json, body);
	}

	[Fact]
	public async Task ReadChunkedBodyAsync_Utf8Content()
	{
		const string text = "café ☕ naïve";
		var bytes = Encoding.UTF8.GetBytes(text);
		var hexSize = bytes.Length.ToString("x");
		var chunkedBytes = Encoding.UTF8.GetBytes($"{hexSize}\r\n")
			.Concat(bytes)
			.Concat("\r\n0\r\n\r\n"u8.ToArray())
			.ToArray();
		using var stream = new MemoryStream(chunkedBytes);

		var body = await McpPipeServer.ReadChunkedBodyAsync(stream, CancellationToken.None);

		Assert.Equal(text, body);
	}

	[Fact]
	public async Task ReadChunkedBodyAsync_StreamEndsEarly_ThrowsEndOfStream()
	{
		// Stream ends mid-chunk — ReadExactlyAsync for trailing \r\n throws
		const string chunked = "FF\r\nShort";
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(chunked));

		await Assert.ThrowsAsync<EndOfStreamException>(
			() => McpPipeServer.ReadChunkedBodyAsync(stream, CancellationToken.None));
	}
}
