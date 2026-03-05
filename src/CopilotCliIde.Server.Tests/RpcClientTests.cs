using CopilotCliIde.Shared;

namespace CopilotCliIde.Server.Tests;

/// <summary>
/// Tests for RpcClient behavior that doesn't require an actual pipe connection.
/// </summary>
public class RpcClientTests
{
	[Fact]
	public async Task RaiseSelectionChanged_NoHandler_DoesNotThrow()
	{
		var client = new RpcClient();

		// No event handler attached — should complete without error
		await client.RaiseSelectionChanged(new SelectionNotification
		{
			Text = "hello",
			FilePath = @"C:\test.cs",
		});
	}

	[Fact]
	public async Task RaiseSelectionChanged_InvokesHandler()
	{
		var client = new RpcClient();
		SelectionNotification? received = null;

		client.SelectionChanged += notification =>
		{
			received = notification;
			return Task.CompletedTask;
		};

		var sent = new SelectionNotification
		{
			Text = "var x = 1;",
			FilePath = @"C:\src\file.cs",
			FileUrl = "file:///C:/src/file.cs",
			Selection = new SelectionRange
			{
				Start = new SelectionPosition { Line = 1, Character = 0 },
				End = new SelectionPosition { Line = 1, Character = 10 },
				IsEmpty = false,
			},
		};

		await client.RaiseSelectionChanged(sent);

		Assert.NotNull(received);
		Assert.Equal("var x = 1;", received!.Text);
		Assert.Equal(@"C:\src\file.cs", received.FilePath);
		Assert.Equal(1, received.Selection!.Start!.Line);
	}

	[Fact]
	public async Task RaiseSelectionChanged_NullNotificationFields_HandledGracefully()
	{
		var client = new RpcClient();
		SelectionNotification? received = null;

		client.SelectionChanged += notification =>
		{
			received = notification;
			return Task.CompletedTask;
		};

		await client.RaiseSelectionChanged(new SelectionNotification());

		Assert.NotNull(received);
		Assert.Null(received!.Text);
		Assert.Null(received.FilePath);
		Assert.Null(received.Selection);
	}

	[Fact]
	public void VsServices_IsNull_BeforeConnect()
	{
		var client = new RpcClient();

		Assert.Null(client.VsServices);
	}

	[Fact]
	public void Dispose_BeforeConnect_DoesNotThrow()
	{
		var client = new RpcClient();
		client.Dispose();
	}

	[Fact]
	public void Dispose_MultipleTimes_DoesNotThrow()
	{
		var client = new RpcClient();
		client.Dispose();
		client.Dispose();
	}
}
