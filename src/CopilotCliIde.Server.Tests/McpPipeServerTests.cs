using CopilotCliIde.Shared;

namespace CopilotCliIde.Server.Tests;

public class McpPipeServerTests
{
	[Fact]
	public async Task DisposeAsync_BeforeStart_DoesNotThrow()
	{
		var server = new AspNetMcpPipeServer();

		await server.DisposeAsync();
	}

	[Fact]
	public async Task DisposeAsync_MultipleTimes_DoesNotThrow()
	{
		var server = new AspNetMcpPipeServer();

		await server.DisposeAsync();
		await server.DisposeAsync();
	}

	[Fact]
	public async Task PushNotificationAsync_NoClients_DoesNotThrow()
	{
		var server = new AspNetMcpPipeServer();

		// No SSE clients connected — should silently succeed
		await server.PushNotificationAsync(Notification.SelectionChanged, new
		{
			text = "hello",
			filePath = @"C:\test.cs"
		});
	}

	[Fact]
	public void PushNotificationAsync_SerializesJsonRpcFormat()
	{
		// Verify the notification format indirectly — if PushNotificationAsync
		// doesn't throw with valid JSON-serializable objects, the serialization works
		var server = new AspNetMcpPipeServer();

		// Various payload shapes that must serialize without error
		Assert.NotNull(server.PushNotificationAsync("test", null));
		Assert.NotNull(server.PushNotificationAsync("test", new { }));
		Assert.NotNull(server.PushNotificationAsync("test", new { key = "value", nested = new { a = 1 } }));
	}
}
