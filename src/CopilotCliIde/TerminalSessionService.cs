namespace CopilotCliIde;

// Package-level singleton that manages the terminal process lifecycle.
// The tool window attaches/detaches from this service — the process survives
// window hide/show cycles and is only torn down on solution close or dispose.
internal sealed class TerminalSessionService(OutputLogger? logger) : IDisposable
{
	private readonly object _processLock = new();
	private TerminalProcess? _process;
	private string? _workingDirectory;
	private short _cols = 120;
	private short _rows = 40;

	// Fired when the terminal produces output (UTF-8 string).
	public event Action<string>? OutputReceived;

	// Fired when the terminal process exits.
	public event Action? ProcessExited;

	// Fired after a session restart completes — signals the UI to clear and re-fit.
	public event Action? SessionRestarted;

	public bool IsRunning => _process?.IsRunning ?? false;

	public void StartSession(string workingDirectory, short cols = 120, short rows = 40)
	{
		lock (_processLock)
		{
			StopSessionCore();

			_workingDirectory = workingDirectory;
			_cols = cols;
			_rows = rows;
			logger?.Log($"Terminal: starting session in {workingDirectory} ({cols}x{rows})");

			try
			{
				_process = new TerminalProcess();
				_process.OutputReceived += OnOutputReceived;
				_process.ProcessExited += OnProcessExited;
				_process.Start(workingDirectory, cols, rows);
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

	public void RestartSession(string? workingDirectory = null)
	{
		var restarted = false;
		lock (_processLock)
		{
			var dir = workingDirectory ?? _workingDirectory;
			if (dir != null)
			{
				StopSessionCore();

				_workingDirectory = dir;
				logger?.Log($"Terminal: restarting session in {dir} ({_cols}x{_rows})");

				try
				{
					_process = new TerminalProcess();
					_process.OutputReceived += OnOutputReceived;
					_process.ProcessExited += OnProcessExited;
					_process.Start(dir, _cols, _rows);
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

		// Fire outside the lock — handler dispatches to UI thread for xterm.js clear + re-fit
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
