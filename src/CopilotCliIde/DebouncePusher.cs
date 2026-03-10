namespace CopilotCliIde;

// 200ms debounce timer with content-based deduplication for push notifications.
internal sealed class DebouncePusher(Action onElapsed) : IDisposable
{
	private Timer? _timer;
	private string? _lastKey;

	public void Schedule()
	{
		if (_timer == null)
			_timer = new Timer(_ => onElapsed(), null, 200, Timeout.Infinite);
		else
			_timer.Change(200, Timeout.Infinite);
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
		_timer?.Dispose();
		_timer = null;
	}

	public void Dispose() => Reset();
}
