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
	private DiagnosticTracker? _diagnosticTracker;
	private OutputLogger? _logger;

	protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
	{
		await base.InitializeAsync(cancellationToken, progress);
		await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

		try
		{
			_logger = OutputLogger.Create();
			VsServiceRpc.Logger = _logger;
			VsServiceRpc.OnResetNotificationState = ResetNotificationState;

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

	// Creates pipes, launches MCP server, writes lock file for CLI discovery.
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

		// Subscribe to Error List data layer for real-time diagnostic notifications
		var componentModel = (IComponentModel)GetGlobalService(typeof(SComponentModel));
		_diagnosticTracker = new DiagnosticTracker(
			componentModel,
			JoinableTaskFactory,
			() => _mcpCallbacks,
			cb => _mcpCallbacks = cb,
			CollectDiagnosticsGrouped,
			_logger);
		_diagnosticTracker.Subscribe();
	}

	// Tears down connection: removes lock file, kills server, disposes pipe.
	private void StopConnection()
	{
		_mcpCallbacks = null;

		_diagnosticTracker?.Dispose();
		_diagnosticTracker = null;
		_selectionTracker?.Reset();

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

	// Clears dedup keys so a newly connected CLI client gets fresh notifications.
	private void ResetNotificationState()
	{
		_selectionTracker?.ResetDedupKey();
		_diagnosticTracker?.ResetDedupKey();
	}

	private void OnBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action) => _diagnosticTracker?.SchedulePush();

	private void OnDocumentSaved(EnvDTE.Document document) => _diagnosticTracker?.SchedulePush();

	private static List<DiagnosticsChangedUri> CollectDiagnosticsGrouped()
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		return [.. ErrorListReader.CollectGrouped().Select(f => new DiagnosticsChangedUri { Uri = f.Uri, Diagnostics = f.Diagnostics })];
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_selectionTracker?.Dispose();

			_solutionEvents?.Opened -= OnSolutionOpened;
			_solutionEvents?.AfterClosing -= OnSolutionAfterClosing;
			_solutionEvents = null;

			_buildEvents?.OnBuildDone -= OnBuildDone;
			_buildEvents = null;
			_documentEvents?.DocumentSaved -= OnDocumentSaved;
			_documentEvents = null;

			StopConnection();

			_discovery?.Dispose();

			if (_selectionMonitorCookie != 0)
			{
#pragma warning disable VSTHRD010
				_monitorSelection?.UnadviseSelectionEvents(_selectionMonitorCookie);
#pragma warning restore VSTHRD010
				_selectionMonitorCookie = 0;
			}
			_monitorSelection = null;
		}
		base.Dispose(disposing);
	}
}
