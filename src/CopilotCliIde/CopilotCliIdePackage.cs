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
	private CancellationTokenSource? _selectionCts;

	protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
	{
		await base.InitializeAsync(cancellationToken, progress);
		await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

		try
		{
			_discovery = new IdeDiscovery();
			await _discovery.CleanStaleFilesAsync();

			var rpcPipeName = $"copilot-cli-rpc-{Guid.NewGuid()}";
			var mcpPipeName = $"mcp-{Guid.NewGuid()}.sock";
			var nonce = Guid.NewGuid().ToString();

			// Start RPC server for VS services
			_ = JoinableTaskFactory.RunAsync(() => StartRpcServerAsync(rpcPipeName, cancellationToken));

			// Start MCP server process
			_processManager = new ServerProcessManager();
			await _processManager.StartAsync(rpcPipeName, mcpPipeName, nonce);

			// Write lock file for Copilot CLI discovery
			var workspaceFolders = GetWorkspaceFolders();
			await _discovery.WriteLockFileAsync(mcpPipeName, nonce, workspaceFolders);

			// Subscribe to solution events to keep lock file in sync
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
			var diagPath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".copilot", "ide", $"vs-error-{Process.GetCurrentProcess().Id}.log");
			Directory.CreateDirectory(Path.GetDirectoryName(diagPath)!);
			File.WriteAllText(diagPath, $"{DateTime.UtcNow:O}\n{ex}");
		}
	}

	private async Task StartRpcServerAsync(string pipeName, CancellationToken ct)
	{
		_rpcPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		await _rpcPipe.WaitForConnectionAsync(ct);
		_rpc = JsonRpc.Attach(_rpcPipe, new VsServiceRpc());
		_mcpCallbacks = _rpc.Attach<IMcpServerCallbacks>();
#pragma warning disable VSTHRD003 // Completion is a long-running task representing the RPC lifetime
		await _rpc.Completion;
#pragma warning restore VSTHRD003
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
			await JoinableTaskFactory.SwitchToMainThreadAsync();
			var folders = GetWorkspaceFolders();
			if (_discovery != null)
				await _discovery.UpdateWorkspaceFoldersAsync(folders);
		});
	}

	private void OnSolutionAfterClosing()
	{
		_ = JoinableTaskFactory.RunAsync(async () =>
		{
			if (_discovery != null)
				await _discovery.UpdateWorkspaceFoldersAsync(new List<string>().AsReadOnly());
		});
	}

	private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
	{
		SchedulePushSelection(debounceMs: 150);
	}

	private void OnLineChanged(EnvDTE.TextPoint startPoint, EnvDTE.TextPoint endPoint, int hint)
	{
		SchedulePushSelection(debounceMs: 50);
	}

	/// <summary>
	/// Debounces selection pushes — cancels any pending push and schedules a new one.
	/// The delay gives the document time to fully load after tab activation.
	/// </summary>
	private void SchedulePushSelection(int debounceMs)
	{
		try
		{
			_selectionCts?.Cancel();
			_selectionCts = new CancellationTokenSource();
			var ct = _selectionCts.Token;

			_ = JoinableTaskFactory.RunAsync(async () =>
			{
				try
				{
					await Task.Delay(debounceMs, ct);
					await PushSelectionAsync(ct);
				}
				catch (OperationCanceledException) { }
				catch { }
			});
		}
		catch { }
	}

	private async Task PushSelectionAsync(CancellationToken ct)
	{
		if (_mcpCallbacks == null) return;

		try
		{
			await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
			var dte = (EnvDTE80.DTE2)GetGlobalService(typeof(EnvDTE.DTE));
			var doc = dte?.ActiveDocument;
			if (doc == null) return;

			string? selectedText = null;
			int startLine = 0, startCol = 0, endLine = 0, endCol = 0;
			bool isEmpty = true;

			// Retry with backoff — the document may not be ready immediately after tab activation
			for (int attempt = 0; attempt < 3; attempt++)
			{
				ct.ThrowIfCancellationRequested();
				try
				{
					if (doc.Object("TextDocument") is EnvDTE.TextDocument textDoc)
					{
						var sel = textDoc.Selection;
						isEmpty = sel.IsEmpty;
						selectedText = isEmpty ? "" : sel.Text;
						if (selectedText?.Length > 10_000) selectedText = selectedText.Substring(0, 10_000);
						startLine = sel.TopPoint.Line - 1;
						startCol = sel.TopPoint.DisplayColumn - 1;
						endLine = sel.BottomPoint.Line - 1;
						endCol = sel.BottomPoint.DisplayColumn - 1;
					}
					break; // success
				}
				catch (COMException) when (attempt < 2)
				{
					await Task.Delay(150 * (attempt + 1), ct);
				}
			}

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

			await Task.Run(async () =>
			{
				try { await _mcpCallbacks.OnSelectionChangedAsync(notification); }
				catch { _mcpCallbacks = null; }
			});
		}
		catch (OperationCanceledException) { throw; }
		catch { /* Don't crash VS on notification failure */ }
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
			_selectionCts?.Cancel();
			_selectionCts?.Dispose();
			_solutionEvents = null;
			_windowEvents = null;
			_textEditorEvents = null;
			_processManager?.Dispose();
			_rpc?.Dispose();
			_rpcPipe?.Dispose();
			_discovery?.Dispose();
		}
		base.Dispose(disposing);
	}
}
