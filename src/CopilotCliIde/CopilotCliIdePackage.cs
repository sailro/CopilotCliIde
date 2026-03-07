using System.IO.Pipes;
using System.Runtime.InteropServices;
using CopilotCliIde.Shared;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
	private EnvDTE.BuildEvents? _buildEvents;
	private EnvDTE.DocumentEvents? _documentEvents;
	private SelectionTracker? _selectionTracker;
	private IVsMonitorSelection? _monitorSelection;
	private uint _selectionMonitorCookie;
	private CancellationTokenSource? _connectionCts;
	private DebouncePusher? _diagnosticsPusher;
	private OutputLogger? _logger;

	protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
	{
		await base.InitializeAsync(cancellationToken, progress);
		await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

		try
		{
			_logger = OutputLogger.Create();
			VsServiceRpc.Logger = _logger;

			_discovery = new IdeDiscovery();
			await _discovery.CleanStaleFilesAsync();

			_logger?.Log("Extension loaded, starting connection...");
			await StartConnectionAsync();

			// Subscribe to solution events to restart connection on solution switch
			var dte = (EnvDTE80.DTE2)GetGlobalService(typeof(EnvDTE.DTE));
			_solutionEvents = dte.Events.SolutionEvents;
			_solutionEvents.Opened += OnSolutionOpened;
			_solutionEvents.AfterClosing += OnSolutionAfterClosing;

			// Subscribe to build/save events for diagnostics push notifications
			_buildEvents = dte.Events.BuildEvents;
			_buildEvents.OnBuildDone += OnBuildDone;
			_documentEvents = dte.Events.DocumentEvents;
			_documentEvents.DocumentSaved += OnDocumentSaved;

			// Track active editor via native VS APIs (no DTE / COM interop)
			var componentModel = (IComponentModel)GetGlobalService(typeof(SComponentModel));
			var editorAdaptersFactory = componentModel.GetService<IVsEditorAdaptersFactoryService>();
			_selectionTracker = new SelectionTracker(editorAdaptersFactory, () => _mcpCallbacks, cb => _mcpCallbacks = cb, _logger);
			_monitorSelection = (IVsMonitorSelection)GetGlobalService(typeof(SVsShellMonitorSelection));
			_monitorSelection.AdviseSelectionEvents(new SelectionTracker.SelectionEventSink(_selectionTracker), out _selectionMonitorCookie);
			_selectionTracker.TrackActiveView();
		}
		catch (Exception ex)
		{
			_logger?.Log($"InitializeAsync failed: {ex.Message}");
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
		_mcpCallbacks = null;

		_selectionTracker?.Reset();
		_diagnosticsPusher?.Reset();

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
				_logger?.Log($"OnSolutionOpened failed: {ex.Message}");
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
				_logger?.Log($"OnSolutionAfterClosing failed: {ex.Message}");
			}
		});
	}

	private void OnBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action) => ScheduleDiagnosticsPush();

	private void OnDocumentSaved(EnvDTE.Document document) => ScheduleDiagnosticsPush();

	/// <summary>
	/// Schedules a diagnostics push with 200ms debounce (same as VS Code).
	/// Called after builds complete and documents are saved.
	/// </summary>
	private void ScheduleDiagnosticsPush()
	{
		if (_mcpCallbacks == null) return;

		_diagnosticsPusher ??= new DebouncePusher(OnDiagnosticsDebounceElapsed);
		_diagnosticsPusher.Schedule();
	}

	/// <summary>
	/// Fires 200ms after the last diagnostics change trigger. Collects
	/// current Error List items on the UI thread and pushes them to the CLI.
	/// Deduplicates by comparing a fingerprint of the diagnostics content.
	/// </summary>
	private void OnDiagnosticsDebounceElapsed()
	{
		var callbacks = _mcpCallbacks;
		if (callbacks == null) return;

		_ = JoinableTaskFactory.RunAsync(async () =>
		{
			try
			{
				await JoinableTaskFactory.SwitchToMainThreadAsync();
				var uris = CollectDiagnosticsGrouped();

				var key = ComputeDiagnosticsKey(uris);
				if (_diagnosticsPusher!.IsDuplicate(key)) return;

				var notification = new DiagnosticsChangedNotification { Uris = uris };
				var totalDiagnostics = uris.Sum(u => u.Diagnostics?.Count ?? 0);
				_logger?.Log($"Push diagnostics_changed: {uris.Count} file(s), {totalDiagnostics} diagnostic(s)");

				await Task.Run(async () =>
				{
					try { await callbacks.OnDiagnosticsChangedAsync(notification); }
					catch { _mcpCallbacks = null; }
				});
			}
			catch { /* Don't crash VS */ }
		});
	}

	private static string ComputeDiagnosticsKey(List<DiagnosticsChangedUri> uris)
	{
		var hash = new HashCode();
		foreach (var group in uris)
		{
			hash.Add(group.Uri);
			foreach (var d in group.Diagnostics!)
			{
				hash.Add(d.Message);
				hash.Add(d.Severity);
				hash.Add(d.Range?.Start?.Line);
				hash.Add(d.Range?.Start?.Character);
			}
		}
		return hash.ToHashCode().ToString();
	}

	private static List<DiagnosticsChangedUri> CollectDiagnosticsGrouped()
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		return ErrorListReader.CollectGrouped()
			.Select(f => new DiagnosticsChangedUri { Uri = f.Uri, Diagnostics = f.Diagnostics })
			.ToList();
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_selectionTracker?.Dispose();
			_solutionEvents = null;
			_buildEvents = null;
			_documentEvents = null;
			StopConnection();
			_discovery?.Dispose();

			if (_selectionMonitorCookie != 0)
			{
				try
				{
					_monitorSelection?.UnadviseSelectionEvents(_selectionMonitorCookie);
				}
				catch { /* Ignore */ }
				_selectionMonitorCookie = 0;
			}
			_monitorSelection = null;
		}
		base.Dispose(disposing);
	}
}
