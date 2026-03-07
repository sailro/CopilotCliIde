namespace CopilotCliIde;

/// <summary>
/// Encapsulates a 200ms debounce timer with content-based deduplication.
/// Used by both selection and diagnostics push notifications to avoid
/// redundant RPC calls and coalesce rapid-fire events.
/// </summary>
internal sealed class DebouncePusher(Action onElapsed) : IDisposable
{
	private Timer? _timer;
	private string? _lastKey;

	/// <summary>
	/// Resets the 200ms debounce window. The callback fires once,
	/// 200ms after the last call to Schedule().
	/// </summary>
	public void Schedule()
	{
		if (_timer == null)
			_timer = new Timer(_ => onElapsed(), null, 200, Timeout.Infinite);
		else
			_timer.Change(200, Timeout.Infinite);
	}

	/// <summary>
	/// Returns true if <paramref name="key"/> matches the last accepted key
	/// (meaning the notification would be redundant). Otherwise records it
	/// as the new last key and returns false.
	/// </summary>
	public bool IsDuplicate(string key)
	{
		if (key == _lastKey)
			return true;

		_lastKey = key;
		return false;
	}

	/// <summary>
	/// Clears the dedup key and disposes the timer. Called on connection
	/// stop so the next connection starts fresh.
	/// </summary>
	public void Reset()
	{
		_lastKey = null;
		_timer?.Dispose();
		_timer = null;
	}

	public void Dispose() => Reset();
}
