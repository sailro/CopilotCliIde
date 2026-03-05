using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using CopilotCliIde.Server;
using CopilotCliIde.Shared;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
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
	private IVsEditorAdaptersFactoryService? _editorAdaptersFactory;
	private IWpfTextView? _trackedView;
	private uint _selectionMonitorCookie;
	private string? _lastSelectionKey;
	private int _notificationVersion;
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

			// Track active editor via native VS APIs (no DTE / COM interop)
			var componentModel = (IComponentModel)GetGlobalService(typeof(SComponentModel));
			_editorAdaptersFactory = componentModel.GetService<IVsEditorAdaptersFactoryService>();
			var monitorSelection = (IVsMonitorSelection)GetGlobalService(typeof(SVsShellMonitorSelection));
			monitorSelection.AdviseSelectionEvents(new SelectionEventSink(this), out _selectionMonitorCookie);
			TrackActiveView();
		}
		catch (Exception ex)
		{
			LogError(ex);
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
		Interlocked.Exchange(ref _notificationVersion, 0);
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

	/// <summary>
	/// Checks the active text view and subscribes to its selection/caret events.
	/// Called when the active window frame changes and on initial load.
	/// When a frame is provided, the text view is obtained directly from it
	/// (avoids IVsTextManager timing issues where GetActiveView hasn't updated yet).
	/// </summary>
	private void TrackActiveView(IVsWindowFrame? frame = null)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		if (_editorAdaptersFactory == null) return;

		IVsTextView? vsTextView = null;

		if (frame != null)
		{
			vsTextView = VsShellUtilities.GetTextView(frame);
		}
		else
		{
			var textManager = (IVsTextManager)GetGlobalService(typeof(SVsTextManager));
			textManager?.GetActiveView(0, null, out vsTextView);
		}

		var wpfView = vsTextView != null ? _editorAdaptersFactory.GetWpfTextView(vsTextView) : null;

		// No text view (e.g., tool window) — keep tracking the current one
		if (wpfView == null) return;
		if (wpfView == _trackedView) return;

		UntrackView();

		_trackedView = wpfView;
		_trackedView.Selection.SelectionChanged += OnEditorSelectionChanged;
		_trackedView.Closed += OnViewClosed;

		PushCurrentSelection();
	}

	private void UntrackView()
	{
		if (_trackedView != null)
		{
			_trackedView.Selection.SelectionChanged -= OnEditorSelectionChanged;
			_trackedView.Closed -= OnViewClosed;
			_trackedView = null;
		}
	}

	private void OnEditorSelectionChanged(object? sender, EventArgs e)
	{
		PushCurrentSelection();
		// Re-read after input processing completes to capture the final state
		// after mouse-up (SelectionChanged may fire with intermediate data during drags)
		_ = JoinableTaskFactory.RunAsync(async () =>
		{
			await Task.Yield();
			PushCurrentSelection();
		});
	}

	private void OnViewClosed(object? sender, EventArgs e) => UntrackView();

	/// <summary>
	/// Reads the current selection from the tracked IWpfTextView and pushes it
	/// to Copilot CLI off the UI thread. Positions are mapped through the
	/// BufferGraph to the document buffer so they match the source file
	/// regardless of any projection/elision view buffers.
	/// </summary>
	private void PushCurrentSelection()
	{
		if (_mcpCallbacks == null || _trackedView == null) return;

		try
		{
			var view = _trackedView;
			var selection = view.Selection;

			// Use the document buffer for canonical positions (not the view buffer
			// which may be a projection/elision buffer from code folding)
			var documentBuffer = view.TextViewModel.DataModel.DocumentBuffer;
			var documentSnapshot = documentBuffer.CurrentSnapshot;

			string? filePath = null;
			if (documentBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument? textDoc))
				filePath = textDoc?.FilePath;
			if (string.IsNullOrEmpty(filePath)) return;

			var isEmpty = selection.IsEmpty;

			// Map selection positions from view buffer to document buffer
			var mappedStart = view.BufferGraph.MapDownToBuffer(
				selection.Start.Position, PointTrackingMode.Positive, documentBuffer, PositionAffinity.Successor);
			var mappedEnd = view.BufferGraph.MapDownToBuffer(
				selection.End.Position, PointTrackingMode.Positive, documentBuffer, PositionAffinity.Successor);
			if (mappedStart == null || mappedEnd == null) return;

			var startLine = mappedStart.Value.GetContainingLine();
			var endLine = mappedEnd.Value.GetContainingLine();
			var startLineNumber = startLine.LineNumber;
			var startCol = mappedStart.Value.Position - startLine.Start.Position;
			var endLineNumber = endLine.LineNumber;
			var endCol = mappedEnd.Value.Position - endLine.Start.Position;

			var selectedText = isEmpty
				? ""
				: documentSnapshot.GetText(mappedStart.Value.Position, mappedEnd.Value.Position - mappedStart.Value.Position);
			if (selectedText.Length > 10_000) selectedText = selectedText.Substring(0, 10_000);

			// Deduplicate — don't push if nothing changed
			var key = $"{filePath}:{startLineNumber}:{startCol}:{endLineNumber}:{endCol}:{isEmpty}";
			if (key == _lastSelectionKey) return;
			_lastSelectionKey = key;

			var notification = new SelectionNotification
			{
				Text = selectedText,
				FilePath = ToLowerDriveLetter(filePath!),
				FileUrl = ToVsCodeFileUrl(filePath!),
				Selection = new SelectionRange
				{
					Start = new SelectionPosition { Line = startLineNumber, Character = startCol },
					End = new SelectionPosition { Line = endLineNumber, Character = endCol },
					IsEmpty = isEmpty
				}
			};

			// Send off the UI thread — version check ensures only the latest notification sends
			var version = Interlocked.Increment(ref _notificationVersion);
			var callbacks = _mcpCallbacks;
			_ = Task.Run(async () =>
			{
				if (version != Interlocked.CompareExchange(ref _notificationVersion, 0, 0)) return;
				try { await callbacks.OnSelectionChangedAsync(notification); }
				catch { _mcpCallbacks = null; }
			});
		}
		catch { /* Don't crash VS */ }
	}

	/// <summary>
	/// Receives IVsMonitorSelection callbacks when the active window frame changes.
	/// Triggers TrackActiveView to subscribe to the new editor's events.
	/// </summary>
	private sealed class SelectionEventSink(CopilotCliIdePackage owner) : IVsSelectionEvents
	{
		public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
			IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew) => VSConstants.S_OK;

		public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (elementid == (uint)VSConstants.VSSELELEMID.SEID_WindowFrame)
				owner.TrackActiveView(varValueNew as IVsWindowFrame);
			return VSConstants.S_OK;
		}

		public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;
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
		ThreadHelper.ThrowIfNotOnUIThread();
		if (disposing)
		{
			UntrackView();
			_solutionEvents = null;
			StopConnection();
			_discovery?.Dispose();

			if (_selectionMonitorCookie != 0)
			{
				try
				{
					var monSel = (IVsMonitorSelection)GetGlobalService(typeof(SVsShellMonitorSelection));
					monSel?.UnadviseSelectionEvents(_selectionMonitorCookie);
				}
				catch { /* Ignore */ }
			}
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
