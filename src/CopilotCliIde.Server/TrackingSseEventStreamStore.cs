using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Threading.Channels;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Server;

internal sealed class TrackingSseEventStreamStore(Func<string, Task>? onStreamCreatedAsync = null) : ISseEventStreamStore
{
	private sealed class StreamState(string sessionId, string streamId)
	{
		private readonly Lock _lock = new();
		private long _sequence;
		private bool _completed;
		private readonly Channel<SseItem<JsonRpcMessage?>> _channel = Channel.CreateUnbounded<SseItem<JsonRpcMessage?>>();
		private readonly List<SseItem<JsonRpcMessage?>> _history = [];

		public string SessionId { get; } = sessionId;
		public string StreamId { get; } = streamId;

		public SseItem<JsonRpcMessage?> AddEvent(SseItem<JsonRpcMessage?> item)
		{
			lock (_lock)
			{
				var sequence = Interlocked.Increment(ref _sequence);
				var eventToWrite = new SseItem<JsonRpcMessage?>(item.Data, item.EventType)
				{
					EventId = string.IsNullOrWhiteSpace(item.EventId) ? $"{SessionId}:{StreamId}:{sequence}" : item.EventId,
					ReconnectionInterval = item.ReconnectionInterval
				};

				if (_completed)
				{
					throw new InvalidOperationException("Event stream is closed.");
				}

				_history.Add(eventToWrite);
				_channel.Writer.TryWrite(eventToWrite);
				return eventToWrite;
			}
		}

		public SseItem<JsonRpcMessage?>[] GetHistorySnapshot()
		{
			lock (_lock)
			{
				return [.. _history];
			}
		}

		public ChannelReader<SseItem<JsonRpcMessage?>> Reader => _channel.Reader;

		public void Complete()
		{
			lock (_lock)
			{
				if (_completed)
				{
					return;
				}

				_completed = true;
				_channel.Writer.TryComplete();
			}
		}
	}

	private sealed class StreamWriter(StreamState state) : ISseEventStreamWriter
	{
		public ValueTask SetModeAsync(SseEventStreamMode mode, CancellationToken cancellationToken)
		{
			return ValueTask.CompletedTask;
		}

		public ValueTask<SseItem<JsonRpcMessage?>> WriteEventAsync(SseItem<JsonRpcMessage?> item, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var written = state.AddEvent(item);
			return ValueTask.FromResult(written);
		}

		public ValueTask DisposeAsync()
		{
			TrackingSseEventStreamStore.OnWriterDisposed(state);
			return ValueTask.CompletedTask;
		}
	}

	private sealed class StreamReader(StreamState state) : ISseEventStreamReader
	{
		public string SessionId => state.SessionId;
		public string StreamId => state.StreamId;
		private long _afterSequence = -1;

		public void SetLastEventId(string? lastEventId)
		{
			if (string.IsNullOrWhiteSpace(lastEventId))
			{
				return;
			}

			var parts = lastEventId.Split(':');
			if (parts.Length != 3)
			{
				return;
			}

			if (long.TryParse(parts[2], out var seq))
			{
				_afterSequence = seq;
			}
		}

		public async IAsyncEnumerable<SseItem<JsonRpcMessage?>> ReadEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
		{
			foreach (var item in state.GetHistorySnapshot())
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (TryGetSequence(item.EventId, out var seq) && seq <= _afterSequence)
				{
					continue;
				}

				yield return item;
			}

			while (await state.Reader.WaitToReadAsync(cancellationToken))
			{
				while (state.Reader.TryRead(out var item))
				{
					if (TryGetSequence(item.EventId, out var seq) && seq <= _afterSequence)
					{
						continue;
					}

					yield return item;
				}
			}
		}

		private static bool TryGetSequence(string? eventId, out long sequence)
		{
			sequence = 0;
			if (string.IsNullOrWhiteSpace(eventId))
			{
				return false;
			}

			var parts = eventId.Split(':');
			return parts.Length == 3 && long.TryParse(parts[2], out sequence);
		}
	}

	private readonly ConcurrentDictionary<string, StreamState> _streamsById = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, StreamState>> _streamsBySession = new(StringComparer.Ordinal);

	public ValueTask<ISseEventStreamWriter> CreateStreamAsync(SseEventStreamOptions options, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var state = new StreamState(options.SessionId, options.StreamId);
		_streamsById[options.StreamId] = state;

		var sessionStreams = _streamsBySession.GetOrAdd(options.SessionId,
			_ => new ConcurrentDictionary<string, StreamState>(StringComparer.Ordinal));
		sessionStreams[options.StreamId] = state;

		if (onStreamCreatedAsync != null)
		{
			_ = Task.Run(async () => await onStreamCreatedAsync(options.SessionId), CancellationToken.None);
		}

		return ValueTask.FromResult<ISseEventStreamWriter>(new StreamWriter(state));
	}

	public ValueTask<ISseEventStreamReader?> GetStreamReaderAsync(string lastEventId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGetStreamId(lastEventId, out var streamId) || !_streamsById.TryGetValue(streamId, out var state))
			return ValueTask.FromResult<ISseEventStreamReader?>(null);

		var reader = new StreamReader(state);
		reader.SetLastEventId(lastEventId);
		return ValueTask.FromResult<ISseEventStreamReader?>(reader);
	}

	public void RemoveSession(string? sessionId)
	{
		if (string.IsNullOrWhiteSpace(sessionId))
		{
			return;
		}

		if (!_streamsBySession.TryRemove(sessionId, out var sessionStreams))
		{
			return;
		}

		foreach (var state in sessionStreams.Values)
		{
			RemoveState(state);
		}
	}

	private static bool TryGetStreamId(string? eventId, out string streamId)
	{
		streamId = string.Empty;
		if (string.IsNullOrWhiteSpace(eventId))
		{
			return false;
		}

		var parts = eventId.Split(':');
		if (parts.Length != 3)
		{
			return false;
		}

		streamId = parts[1];
		return !string.IsNullOrWhiteSpace(streamId);
	}

	private void RemoveState(StreamState state)
	{
		state.Complete();
		_streamsById.TryRemove(state.StreamId, out _);
		if (_streamsBySession.TryGetValue(state.SessionId, out var sessionStreams))
		{
			sessionStreams.TryRemove(state.StreamId, out _);
			if (sessionStreams.IsEmpty)
			{
				_streamsBySession.TryRemove(state.SessionId, out _);
			}
		}
	}

	private static void OnWriterDisposed(StreamState _)
	{
		// Writer lifetimes can be request-scoped in ASP.NET transport. Keep stream state alive
		// so server-initiated notifications can still be delivered until session teardown.
	}
}
