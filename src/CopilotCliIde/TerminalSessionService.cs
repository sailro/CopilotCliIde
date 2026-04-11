namespace CopilotCliIde;

// Package-level singleton that manages the terminal process lifecycle.
// The tool window attaches/detaches from this service — the process survives
// window hide/show cycles and is only torn down on solution close or dispose.
internal sealed class TerminalSessionService : IDisposable
{
	private TerminalProcess? _process;
	private string? _workingDirectory;
	private short _cols = 120;
	private short _rows = 40;
	private readonly OutputLogger? _logger;

	// Fired when the terminal produces output (UTF-8 string).
	public event Action<string>? OutputReceived;

	// Fired when the terminal process exits.
	public event Action? ProcessExited;

	public bool IsRunning => _process?.IsRunning ?? false;

	public TerminalSessionService(OutputLogger? logger)
	{
		_logger = logger;
	}

	public void StartSession(string workingDirectory, short cols = 120, short rows = 40)
	{
		StopSession();

		_workingDirectory = workingDirectory;
		_cols = cols;
		_rows = rows;
		_logger?.Log($"Terminal: starting session in {workingDirectory} ({cols}x{rows})");

		try
		{
			_process = new TerminalProcess();
			_process.OutputReceived += OnOutputReceived;
			_process.ProcessExited += OnProcessExited;
			_process.Start(workingDirectory, cols, rows);
		}
		catch (Exception ex)
		{
			_logger?.Log($"Terminal: failed to start session: {ex.Message}");
			_process?.Dispose();
			_process = null;
		}
	}

	public void StopSession()
	{
		if (_process == null)
			return;

		_logger?.Log("Terminal: stopping session");
		_process.OutputReceived -= OnOutputReceived;
		_process.ProcessExited -= OnProcessExited;
		_process.Dispose();
		_process = null;
	}

	public void RestartSession()
	{
		var dir = _workingDirectory;
		if (dir != null)
			StartSession(dir, _cols, _rows);
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
		_logger?.Log("Terminal: process exited");
		ProcessExited?.Invoke();
	}

	public void Dispose()
	{
		StopSession();
	}
}
