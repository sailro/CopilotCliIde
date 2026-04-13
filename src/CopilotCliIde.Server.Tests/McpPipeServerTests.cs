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
	public async Task PushNotificationAsync_CompletesWithVariousPayloads()
	{
		var server = new AspNetMcpPipeServer();

		// Various payload shapes that must serialize and complete without error
		await server.PushNotificationAsync("test", null);
		await server.PushNotificationAsync("test", new { });
		await server.PushNotificationAsync("test", new { key = "value", nested = new { a = 1 } });
	}
}
