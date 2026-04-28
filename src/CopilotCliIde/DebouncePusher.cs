namespace CopilotCliIde;

// 200ms debounce timer with content-based deduplication for push notifications.
// Timer is created once (dormant) and reused — Schedule() just reschedules it.
internal sealed class DebouncePusher(Action onElapsed) : IDisposable
{
	private readonly Timer _timer = new(_ => onElapsed(), null, Timeout.Infinite, Timeout.Infinite);
	private volatile string? _lastKey;
	private volatile bool _disposed;

	public void Schedule()
	{
		if (_disposed)
			return;

		try { _timer.Change(200, Timeout.Infinite); }
		catch (ObjectDisposedException) { /* Raced with Dispose */ }
	}

	public bool IsDuplicate(string key)
	{
		if (key == _lastKey)
			return true;

		_lastKey = key;
		return false;
	}

	public void ResetDedupKey() => _lastKey = null;

	public void Reset()
	{
		_lastKey = null;

		if (_disposed)
			return;

		try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
		catch (ObjectDisposedException) { /* Raced with Dispose */ }
	}

	public void Dispose()
	{
		_disposed = true;
		_timer.Dispose();
	}
}
