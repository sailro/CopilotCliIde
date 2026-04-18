using System.Text;

namespace CopilotCliIde;

// Manages a Copilot CLI process hosted inside a ConPTY pseudo-console.
// Provides async output reading with batching, input writing, and resize support.
internal sealed class TerminalProcess : IDisposable
{
	private ConPty.Session? _session;
	private Thread? _readThread;
	private CancellationTokenSource? _cts;
	private readonly object _lock = new();
	private bool _disposed;
	private bool _exited;

	// Output batching: accumulate reads, flush on timer (~16ms / 60fps)
	private readonly StringBuilder _outputBuffer = new();
	private readonly object _bufferLock = new();
	private Timer? _flushTimer;

	// Stateful decoder preserves incomplete multi-byte UTF-8 sequences across reads
	private Decoder? _utf8Decoder;

	// Fired when terminal output is available (UTF-8 string, batched).
	public event Action<string>? OutputReceived;

	// Fired when the hosted process exits.
	public event Action? ProcessExited;

	public bool IsRunning
	{
		get
		{
			lock (_lock)
			{
				return _session != null && !_disposed && !_exited;
			}
		}
	}

	public void Start(string workingDirectory, short cols = 120, short rows = 40, string? resumeSessionId = null)
	{
		lock (_lock)
		{
			if (_disposed)
				throw new ObjectDisposedException(nameof(TerminalProcess));

			if (_session != null)
				throw new InvalidOperationException("Process is already running.");

			_cts = new CancellationTokenSource();

			// resumeSessionId is validated by callers (SessionId.IsValid). We re-check here
			// as a defense-in-depth measure since this string is interpolated into a cmd.exe
			// command line. Anything not matching the strict UUID format is dropped.
			var commandLine = SessionId.IsValid(resumeSessionId)
				? $"cmd.exe /c copilot --resume={resumeSessionId}"
				: "cmd.exe /c copilot";

			_session = ConPty.Create(commandLine, workingDirectory, cols, rows);
			_utf8Decoder = Encoding.UTF8.GetDecoder();
			_flushTimer = new Timer(FlushOutput, null, Timeout.Infinite, Timeout.Infinite);

			_readThread = new Thread(ReadLoop)
			{
				IsBackground = true,
				Name = "ConPTY Read"
			};
			_readThread.Start();
		}
	}

	public void WriteInput(string data)
	{
		lock (_lock)
		{
			if (_session == null)
				return;

			var bytes = Encoding.UTF8.GetBytes(data);
			ConPty.Write(_session.InputWriteHandle, bytes, bytes.Length);
		}
	}

	public void Resize(short cols, short rows)
	{
		lock (_lock)
		{
			if (_session == null)
				return;

			ConPty.Resize(_session.PseudoConsoleHandle, cols, rows);
		}
	}

	private void ReadLoop()
	{
		var buffer = new byte[4096];
		var ct = _cts!.Token;

		try
		{
			while (!ct.IsCancellationRequested)
			{
				IntPtr outputHandle;
				lock (_lock)
				{
					if (_session == null)
						break;
					outputHandle = _session.OutputReadHandle;
				}

				var bytesRead = ConPty.Read(outputHandle, buffer);
				if (bytesRead <= 0)
					break;

				var charCount = _utf8Decoder!.GetCharCount(buffer, 0, bytesRead);
				var chars = new char[charCount];

				_utf8Decoder.GetChars(buffer, 0, bytesRead, chars, 0);
				var text = new string(chars);

				lock (_bufferLock)
				{
					_outputBuffer.Append(text);
					// Schedule flush after 16ms if not already scheduled
					_flushTimer?.Change(16, Timeout.Infinite);
				}
			}
		}
		catch (Exception) when (ct.IsCancellationRequested)
		{
			// Expected during shutdown
		}
		catch (Exception)
		{
			// Pipe broken or process exited
		}

		// Flush any remaining buffered output
		FlushOutput(null);
		lock (_lock)
		{
			_exited = true;
		}
		ProcessExited?.Invoke();
	}

	private void FlushOutput(object? state)
	{
		string batch;
		lock (_bufferLock)
		{
			if (_outputBuffer.Length == 0)
				return;
			batch = _outputBuffer.ToString();
			_outputBuffer.Clear();
		}

		OutputReceived?.Invoke(batch);
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed)
				return;
			_disposed = true;

			_cts?.Cancel();
			_flushTimer?.Dispose();
			_flushTimer = null;

			var session = _session;
			_session = null;
			session?.Dispose();

			_cts?.Dispose();
			_cts = null;
		}

		// Wait for read thread outside lock to avoid deadlock
		_readThread?.Join(3000);
		_readThread = null;
	}
}
