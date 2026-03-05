using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using CopilotCliIde.Server;
using CopilotCliIde.Shared;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using StreamJsonRpc;
using Task = System.Threading.Tasks.Task;

namespace CopilotCliIde;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideBindingPath]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
[Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d")]
public sealed class CopilotCliIdePackage : AsyncPackage
{
	private IdeDiscovery? _discovery;
	private ServerProcessManager? _processManager;
	private NamedPipeServerStream? _rpcPipe;
	private JsonRpc? _rpc;
	private IMcpServerCallbacks? _mcpCallbacks;
	private EnvDTE.SolutionEvents? _solutionEvents;
	private EnvDTE.WindowEvents? _windowEvents;
	private EnvDTE.TextEditorEvents? _textEditorEvents;
	private string? _lastSelectionKey;
	private CancellationTokenSource? _connectionCts;

	protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
	{
		await base.InitializeAsync(cancellationToken, progress);
		await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

		try
		{
			_discovery = new IdeDiscovery();
			await _discovery.CleanStaleFilesAsync();

			await StartConnectionAsync();

			// Subscribe to solution events to restart connection on solution switch
			var dte = (EnvDTE80.DTE2)GetGlobalService(typeof(EnvDTE.DTE));
			_solutionEvents = dte.Events.SolutionEvents;
			_solutionEvents.Opened += OnSolutionOpened;
			_solutionEvents.AfterClosing += OnSolutionAfterClosing;

			// Subscribe to editor events to push selection changes to CLI
			_windowEvents = dte.Events.WindowEvents;
			_windowEvents.WindowActivated += OnWindowActivated;
			_textEditorEvents = dte.Events.TextEditorEvents;
			_textEditorEvents.LineChanged += OnLineChanged;
		}
		catch (Exception ex)
		{
			var diagPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "ide", $"vs-error-{Process.GetCurrentProcess().Id}.log");
			Directory.CreateDirectory(Path.GetDirectoryName(diagPath)!);
			File.WriteAllText(diagPath, $"{DateTime.UtcNow:O}\n{ex}");
		}
	}

	/// <summary>
	/// Creates new pipes, starts the MCP server process, and writes a lock file
	/// so Copilot CLI can discover this VS instance. Called on first load and
	/// each time a solution is opened.
	/// </summary>
	private async Task StartConnectionAsync()
	{
		await JoinableTaskFactory.SwitchToMainThreadAsync();
		StopConnection();

		_connectionCts = new CancellationTokenSource();

		var rpcPipeName = $"copilot-cli-rpc-{Guid.NewGuid()}";
		var mcpPipeName = $"mcp-{Guid.NewGuid()}.sock";
		var nonce = Guid.NewGuid().ToString();

		// Start RPC server for VS services
		_ = JoinableTaskFactory.RunAsync(() => StartRpcServerAsync(rpcPipeName, _connectionCts.Token));

		// Start MCP server process
		_processManager = new ServerProcessManager();
		await _processManager.StartAsync(rpcPipeName, mcpPipeName, nonce);

		// Write lock file for Copilot CLI discovery
		var workspaceFolders = GetWorkspaceFolders();
		await _discovery!.WriteLockFileAsync(mcpPipeName, nonce, workspaceFolders);
	}

	/// <summary>
	/// Tears down the current connection: removes the lock file, kills the MCP
	/// server process, and disposes the RPC pipe. Copilot CLI will see the lock
	/// file disappear and disconnect — matching VS Code's close-folder behavior.
	/// </summary>
	private void StopConnection()
	{
		_lastSelectionKey = null;
		_mcpCallbacks = null;

		_connectionCts?.Cancel();
		_connectionCts?.Dispose();
		_connectionCts = null;

		_rpc?.Dispose();
		_rpc = null;
		_rpcPipe?.Dispose();
		_rpcPipe = null;

		_processManager?.Dispose();
		_processManager = null;

		_discovery?.RemoveLockFile();
	}

	private async Task StartRpcServerAsync(string pipeName, CancellationToken ct)
	{
		try
		{
			_rpcPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
			await _rpcPipe.WaitForConnectionAsync(ct);
			_rpc = JsonRpc.Attach(_rpcPipe, new VsServiceRpc());
			_mcpCallbacks = _rpc.Attach<IMcpServerCallbacks>();
#pragma warning disable VSTHRD003 // Completion is a long-running task representing the RPC lifetime
			await _rpc.Completion;
#pragma warning restore VSTHRD003
		}
		catch (OperationCanceledException) { }
		catch (ObjectDisposedException) { }
	}

	private static IReadOnlyList<string> GetWorkspaceFolders()
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		try
		{
			var dte = (EnvDTE80.DTE2)GetGlobalService(typeof(EnvDTE.DTE));
			if (dte?.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
			{
				var dir = Path.GetDirectoryName(dte.Solution.FullName);
				if (!string.IsNullOrEmpty(dir))
					return new List<string> { dir }.AsReadOnly();
			}
		}
		catch { /* Ignore */ }
		return new List<string> { Directory.GetCurrentDirectory() }.AsReadOnly();
	}

	private void OnSolutionOpened()
	{
		_ = JoinableTaskFactory.RunAsync(async () =>
		{
			try
			{
				await JoinableTaskFactory.SwitchToMainThreadAsync();
				await StartConnectionAsync();
			}
			catch (Exception ex)
			{
				LogError(ex);
			}
		});
	}

	private void OnSolutionAfterClosing()
	{
		_ = JoinableTaskFactory.RunAsync(async () =>
		{
			try
			{
				await JoinableTaskFactory.SwitchToMainThreadAsync();
				StopConnection();
			}
			catch (Exception ex)
			{
				LogError(ex);
			}
		});
	}

	private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		PushSelectionFireAndForget();
	}

	private void OnLineChanged(EnvDTE.TextPoint startPoint, EnvDTE.TextPoint endPoint, int hint)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		PushSelectionFireAndForget();
	}

	/// <summary>
	/// Reads the current selection on the UI thread and pushes it to Copilot CLI
	/// in the background. No debouncing — the event handlers fire, we read
	/// immediately, and deduplication prevents redundant sends.
	/// If the document isn't ready yet (COMException on tab activation), we skip
	/// silently — the next LineChanged will pick it up once the editor is loaded.
	/// </summary>
	private void PushSelectionFireAndForget()
	{
		if (_mcpCallbacks == null) return;

		try
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			var dte = (EnvDTE80.DTE2)GetGlobalService(typeof(EnvDTE.DTE));
			var doc = dte?.ActiveDocument;
			if (doc == null) return;

			if (doc.Object("TextDocument") is not EnvDTE.TextDocument textDoc)
				return;

			var sel = textDoc.Selection;
			var isEmpty = sel.IsEmpty;
			var selectedText = isEmpty ? "" : sel.Text;
			if (selectedText?.Length > 10_000) selectedText = selectedText.Substring(0, 10_000);
			var startLine = sel.TopPoint.Line - 1;
			var startCol = sel.TopPoint.DisplayColumn - 1;
			var endLine = sel.BottomPoint.Line - 1;
			var endCol = sel.BottomPoint.DisplayColumn - 1;

			// Deduplicate — don't push if nothing changed
			var key = $"{doc.FullName}:{startLine}:{startCol}:{endLine}:{endCol}:{isEmpty}";
			if (key == _lastSelectionKey) return;
			_lastSelectionKey = key;

			var notification = new SelectionNotification
			{
				Text = selectedText ?? "",
				FilePath = ToLowerDriveLetter(doc.FullName),
				FileUrl = ToVsCodeFileUrl(doc.FullName),
				Selection = new SelectionRange
				{
					Start = new SelectionPosition { Line = startLine, Character = startCol },
					End = new SelectionPosition { Line = endLine, Character = endCol },
					IsEmpty = isEmpty
				}
			};

			// Send off the UI thread so we never block VS
			var callbacks = _mcpCallbacks;
			_ = Task.Run(async () =>
			{
				try { await callbacks.OnSelectionChangedAsync(notification); }
				catch { _mcpCallbacks = null; }
			});
		}
		catch (COMException) { /* Document not ready yet — next event will catch it */ }
		catch { /* Don't crash VS */ }
	}

	/// <summary>
	/// Formats a file path as a VS Code-compatible file URI (lowercase drive, encoded colon).
	/// e.g. C:\Dev\file.cs → file:///c%3A/Dev/file.cs
	/// </summary>
	private static string ToVsCodeFileUrl(string filePath)
	{
		var path = filePath.Replace('\\', '/');
		if (path.Length >= 2 && path[1] == ':')
			path = char.ToLowerInvariant(path[0]) + "%3A" + path.Substring(2);
		return "file:///" + path;
	}

	private static string ToLowerDriveLetter(string filePath)
	{
		if (filePath.Length >= 2 && filePath[1] == ':' && char.IsUpper(filePath[0]))
			return char.ToLowerInvariant(filePath[0]) + filePath.Substring(1);
		return filePath;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_solutionEvents = null;
			_windowEvents = null;
			_textEditorEvents = null;
			StopConnection();
			_discovery?.Dispose();
		}
		base.Dispose(disposing);
	}

	private static void LogError(Exception ex)
	{
		try
		{
			var diagPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "ide", $"vs-error-{Process.GetCurrentProcess().Id}.log");
			Directory.CreateDirectory(Path.GetDirectoryName(diagPath)!);
			File.AppendAllText(diagPath, $"{DateTime.UtcNow:O}\n{ex}\n\n");
		}
		catch { /* Ignore */ }
	}
}
