namespace CopilotCliIde;

// 200ms debounce timer with content-based deduplication for push notifications.
// Timer is created once (dormant) and reused — Schedule() just reschedules it.
internal sealed class DebouncePusher(Action onElapsed) : IDisposable
{
	private readonly Timer _timer = new(_ => onElapsed(), null, Timeout.Infinite, Timeout.Infinite);
	private volatile string? _lastKey;

	public void Schedule() => _timer.Change(200, Timeout.Infinite);

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
		_timer.Change(Timeout.Infinite, Timeout.Infinite);
	}

	public void Dispose() => _timer.Dispose();
}
