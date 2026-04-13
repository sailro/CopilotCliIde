using System.Net.ServerSentEvents;
using System.Reflection;
using System.Threading.Channels;
using CopilotCliIde.Shared;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server.Tests;

public class TrackingSseEventStreamStoreTests
{
	private const string SessionId = "test-session";
	private const string StreamId = "test-stream";

	#region Helpers

	private static async Task<(TrackingSseEventStreamStore Store, ISseEventStreamWriter Writer)> CreateStoreAndWriterAsync()
	{
		var store = new TrackingSseEventStreamStore();
		var writer = await store.CreateStreamAsync(
			new SseEventStreamOptions { SessionId = SessionId, StreamId = StreamId },
			CancellationToken.None);
		return (store, writer);
	}

	private static SseItem<JsonRpcMessage?> MakeNotification(string method) =>
		new(new JsonRpcNotification { Method = method }, "message");

	private static SseItem<JsonRpcMessage?> MakeRequest(string method, string? id = null) =>
		new(new JsonRpcRequest { Method = method, Id = new RequestId(id ?? Guid.NewGuid().ToString()) }, "message");

	private static SseItem<JsonRpcMessage?> MakeResponse(string? id = null) =>
		new(new JsonRpcResponse { Id = new RequestId(id ?? Guid.NewGuid().ToString()), Result = null }, "message");

	/// <summary>
	/// Uses reflection to access the private StreamState and call GetHistorySnapshot().
	/// This is necessary because StreamState is a private nested class — the only way
	/// to directly verify history trimming without conflating channel and history items.
	/// </summary>
	private static SseItem<JsonRpcMessage?>[] GetHistory(TrackingSseEventStreamStore store)
	{
		var field = typeof(TrackingSseEventStreamStore)
			.GetField("_streamsById", BindingFlags.NonPublic | BindingFlags.Instance)!;
		var dict = field.GetValue(store)!;
		var tryGetValue = dict.GetType().GetMethod("TryGetValue")!;
		var args = new object?[] { StreamId, null };
		var found = (bool)tryGetValue.Invoke(dict, args)!;
		Assert.True(found, "Stream not found in _streamsById");

		var state = args[1]!;
		var method = state.GetType().GetMethod("GetHistorySnapshot")!;
		return (SseItem<JsonRpcMessage?>[])method.Invoke(state, null)!;
	}

	/// <summary>
	/// Uses reflection to access the channel reader for verifying all events are pushed to live clients.
	/// </summary>
	private static ChannelReader<SseItem<JsonRpcMessage?>> GetChannelReader(TrackingSseEventStreamStore store)
	{
		var field = typeof(TrackingSseEventStreamStore)
			.GetField("_streamsById", BindingFlags.NonPublic | BindingFlags.Instance)!;
		var dict = field.GetValue(store)!;
		var tryGetValue = dict.GetType().GetMethod("TryGetValue")!;
		var args = new object?[] { StreamId, null };
		tryGetValue.Invoke(dict, args);

		var state = args[1]!;
		var prop = state.GetType().GetProperty("Reader")!;
		return (ChannelReader<SseItem<JsonRpcMessage?>>)prop.GetValue(state)!;
	}

	#endregion

	[Fact]
	public async Task SingleNotification_StoredInHistory()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();

		await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);

		var history = GetHistory(store);
		Assert.Single(history);
		var data = Assert.IsType<JsonRpcNotification>(history[0].Data);
		Assert.Equal(Notification.SelectionChanged, data.Method);
	}

	[Fact]
	public async Task SameTypeNotification_ReplacesPrevious()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();

		await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);
		var second = await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);

		var history = GetHistory(store);
		Assert.Single(history);
		Assert.Equal(second.EventId, history[0].EventId);
	}

	[Fact]
	public async Task DifferentTypeNotifications_BothCoexist()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();

		await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);
		await writer.WriteEventAsync(MakeNotification(Notification.DiagnosticsChanged), CancellationToken.None);

		var history = GetHistory(store);
		Assert.Equal(2, history.Length);

		var methods = history
			.Select(h => ((JsonRpcNotification)h.Data!).Method)
			.OrderBy(m => m)
			.ToArray();
		Assert.Equal(Notification.DiagnosticsChanged, methods[0]);
		Assert.Equal(Notification.SelectionChanged, methods[1]);
	}

	[Fact]
	public async Task MultipleSameTypeNotifications_OnlyKeepsLast()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();

		SseItem<JsonRpcMessage?> lastWritten = default;
		for (var i = 0; i < 5; i++)
		{
			lastWritten = await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);
		}

		var history = GetHistory(store);
		Assert.Single(history);
		Assert.Equal(lastWritten.EventId, history[0].EventId);
	}

	[Fact]
	public async Task NonNotificationMessages_NotTrimmed()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();

		var first = await writer.WriteEventAsync(MakeRequest("tools/call"), CancellationToken.None);
		var second = await writer.WriteEventAsync(MakeRequest("tools/call"), CancellationToken.None);

		var history = GetHistory(store);
		Assert.Equal(2, history.Length);
		Assert.Equal(first.EventId, history[0].EventId);
		Assert.Equal(second.EventId, history[1].EventId);
	}

	[Fact]
	public async Task Mixed_NotificationsTrimmed_NonNotificationsPreserved()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();

		var request = await writer.WriteEventAsync(MakeRequest("tools/call"), CancellationToken.None);
		await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);
		var selLatest = await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);
		await writer.WriteEventAsync(MakeNotification(Notification.DiagnosticsChanged), CancellationToken.None);
		var diagLatest = await writer.WriteEventAsync(MakeNotification(Notification.DiagnosticsChanged), CancellationToken.None);

		var history = GetHistory(store);

		// 1 request (not trimmed) + 1 selection (latest) + 1 diagnostics (latest) = 3
		Assert.Equal(3, history.Length);

		Assert.Equal(request.EventId, history[0].EventId);
		Assert.IsType<JsonRpcRequest>(history[0].Data);

		Assert.Equal(selLatest.EventId, history[1].EventId);
		Assert.Equal(Notification.SelectionChanged, ((JsonRpcNotification)history[1].Data!).Method);

		Assert.Equal(diagLatest.EventId, history[2].EventId);
		Assert.Equal(Notification.DiagnosticsChanged, ((JsonRpcNotification)history[2].Data!).Method);
	}

	[Fact]
	public async Task GetHistorySnapshot_ReturnsCorrectItemsAfterTrimming()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();

		var response = await writer.WriteEventAsync(MakeResponse(), CancellationToken.None);
		await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);
		await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);
		var selFinal = await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);
		var diag = await writer.WriteEventAsync(MakeNotification(Notification.DiagnosticsChanged), CancellationToken.None);

		var snapshot = GetHistory(store);

		// response + latest selection + diagnostics = 3
		Assert.Equal(3, snapshot.Length);
		Assert.Equal(response.EventId, snapshot[0].EventId);
		Assert.Equal(selFinal.EventId, snapshot[1].EventId);
		Assert.Equal(diag.EventId, snapshot[2].EventId);

		// Verify snapshot is a copy — mutating it shouldn't affect the store
		var snapshotBefore = snapshot.Length;
		snapshot[0] = default;
		var snapshotAfter = GetHistory(store);
		Assert.Equal(snapshotBefore, snapshotAfter.Length);
	}

	[Fact]
	public async Task ChannelReceivesAllEvents_EvenWhenHistoryTrims()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();
		var channelReader = GetChannelReader(store);

		const int totalWrites = 5;
		for (var i = 0; i < totalWrites; i++)
		{
			await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);
		}

		// History should have only 1 (the latest selection notification)
		var history = GetHistory(store);
		Assert.Single(history);

		// Channel should have received ALL 5 events
		var channelItems = new List<SseItem<JsonRpcMessage?>>();
		while (channelReader.TryRead(out var item))
		{
			channelItems.Add(item);
		}
		Assert.Equal(totalWrites, channelItems.Count);

		// All channel items should be selection notifications
		Assert.All(channelItems, item =>
		{
			var n = Assert.IsType<JsonRpcNotification>(item.Data);
			Assert.Equal(Notification.SelectionChanged, n.Method);
		});
	}

	[Fact]
	public async Task ResponseMessages_NotTrimmed()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();

		var first = await writer.WriteEventAsync(MakeResponse("resp-1"), CancellationToken.None);
		var second = await writer.WriteEventAsync(MakeResponse("resp-2"), CancellationToken.None);

		var history = GetHistory(store);
		Assert.Equal(2, history.Length);
		Assert.IsType<JsonRpcResponse>(history[0].Data);
		Assert.IsType<JsonRpcResponse>(history[1].Data);
	}

	[Fact]
	public async Task NullDataEvents_NotTrimmed()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();

		await writer.WriteEventAsync(new SseItem<JsonRpcMessage?>(null, "message"), CancellationToken.None);
		await writer.WriteEventAsync(new SseItem<JsonRpcMessage?>(null, "message"), CancellationToken.None);

		var history = GetHistory(store);
		Assert.Equal(2, history.Length);
		Assert.All(history, item => Assert.Null(item.Data));
	}

	[Fact]
	public async Task EventIds_AreSequential()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();

		var first = await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);
		var second = await writer.WriteEventAsync(MakeNotification(Notification.DiagnosticsChanged), CancellationToken.None);

		// EventId format: {SessionId}:{StreamId}:{sequence}
		Assert.Equal($"{SessionId}:{StreamId}:1", first.EventId);
		Assert.Equal($"{SessionId}:{StreamId}:2", second.EventId);
	}

	[Fact]
	public async Task WritingToCompletedStream_Throws()
	{
		var (store, writer) = await CreateStoreAndWriterAsync();
		await writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None);

		// RemoveSession completes the stream
		store.RemoveSession(SessionId);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			writer.WriteEventAsync(MakeNotification(Notification.SelectionChanged), CancellationToken.None).AsTask());
	}
}
