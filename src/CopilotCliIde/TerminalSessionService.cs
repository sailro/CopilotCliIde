namespace CopilotCliIde;

// Package-level singleton that manages the terminal process lifecycle.
// The tool window attaches/detaches from this service — the process survives
// window hide/show cycles and is only torn down on solution close or dispose.
internal sealed class TerminalSessionService(OutputLogger? logger) : IDisposable
{
	private readonly object _processLock = new();
	private TerminalProcess? _process;
	private string? _workingDirectory;
	// Last launch parameters — preserved across implicit restarts (e.g., Enter-to-restart
	// after the CLI exits) so a resumed session stays resumed instead of becoming a fresh one.
	private string? _lastResumeSessionId;

	// Fired when the terminal produces output (UTF-8 string).
	public event Action<string>? OutputReceived;

	// Fired when the terminal process exits.
	public event Action? ProcessExited;

	// Fired after a session restart completes — signals the UI to clear and re-fit.
	public event Action? SessionRestarted;

	public bool IsRunning => _process?.IsRunning ?? false;

	// The session id that the currently running CLI was launched with via --resume,
	// or null if the CLI is running without --resume (a fresh session whose id was
	// chosen by copilot internally). Updated by RestartResuming / RestartFresh.
	public string? LastResumeSessionId => _lastResumeSessionId;

	public void StartSession(string workingDirectory, short cols = 120, short rows = 40)
	{
		lock (_processLock)
		{
			StopSessionCore();

			_workingDirectory = workingDirectory;
			// Initial start uses whatever resume id was previously selected (typically null).
			// Toolbar actions go through RestartFresh / RestartResuming and update this state.
			logger?.Log($"Terminal: starting session in {workingDirectory} ({cols}x{rows}){(_lastResumeSessionId != null ? " resume=" + _lastResumeSessionId : "")}");

			try
			{
				_process = new TerminalProcess();
				_process.OutputReceived += OnOutputReceived;
				_process.ProcessExited += OnProcessExited;
				_process.Start(workingDirectory, cols, rows, _lastResumeSessionId);
			}
			catch (Exception ex)
			{
				logger?.Log($"Terminal: failed to start session: {ex.Message}");
				_process?.Dispose();
				_process = null;
			}
		}
	}

	public void StopSession()
	{
		lock (_processLock)
		{
			StopSessionCore();
		}
	}

	// Must be called under _processLock
	private void StopSessionCore()
	{
		if (_process == null)
			return;

		logger?.Log("Terminal: stopping session");
		_process.OutputReceived -= OnOutputReceived;
		_process.ProcessExited -= OnProcessExited;
		_process.Dispose();
		_process = null;
		_workingDirectory = null;
	}

	// Restarts preserving the last launch mode (resume id is preserved).
	// Used by Enter-to-restart after the CLI exits.
	public void RestartPreservingMode() =>
		RestartCore(workingDirectory: null, _lastResumeSessionId);

	// Restarts in a brand-new session (no --resume). Updates persistent state.
	public void RestartFresh(string workingDirectory)
	{
		_lastResumeSessionId = null;
		RestartCore(workingDirectory, resumeSessionId: null);
	}

	// Restarts resuming the specified session. Updates persistent state.
	public void RestartResuming(string workingDirectory, string sessionId)
	{
		if (!SessionId.IsValid(sessionId))
			throw new ArgumentException("Invalid session id format.", nameof(sessionId));
		_lastResumeSessionId = sessionId;
		RestartCore(workingDirectory, resumeSessionId: sessionId);
	}

	private void RestartCore(string? workingDirectory, string? resumeSessionId)
	{
		var restarted = false;
		lock (_processLock)
		{
			var dir = workingDirectory ?? _workingDirectory;
			if (dir != null)
			{
				StopSessionCore();

				_workingDirectory = dir;
				logger?.Log($"Terminal: restarting session in {dir}{(resumeSessionId != null ? " resume=" + resumeSessionId : "")}");

				try
				{
					_process = new TerminalProcess();
					_process.OutputReceived += OnOutputReceived;
					_process.ProcessExited += OnProcessExited;
					// Use defaults — the new TerminalControl's first Resize will
					// set the real dimensions, forcing the process to redraw.
					_process.Start(dir, resumeSessionId: resumeSessionId);
					restarted = true;
				}
				catch (Exception ex)
				{
					logger?.Log($"Terminal: failed to restart session: {ex.Message}");
					_process?.Dispose();
					_process = null;
				}
			}
		}

		// Fire outside the lock — handler signals the UI to clear and re-fit
		if (restarted)
			SessionRestarted?.Invoke();
	}

	public void WriteInput(string data)
	{
		_process?.WriteInput(data);
	}

	public void Resize(short cols, short rows)
	{
		_process?.Resize(cols, rows);
	}

	private void OnOutputReceived(string data)
	{
		OutputReceived?.Invoke(data);
	}

	private void OnProcessExited()
	{
		logger?.Log("Terminal: process exited");
		ProcessExited?.Invoke();
	}

	public void Dispose()
	{
		StopSession();
	}
}
