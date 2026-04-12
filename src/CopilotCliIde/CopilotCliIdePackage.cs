using System.ComponentModel.Design;
using System.Diagnostics;
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
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(TerminalToolWindow), Transient = true, Style = VsDockStyle.Tabbed, Window = "34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3")]
[Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d")]
public sealed class CopilotCliIdePackage : AsyncPackage
{
	static CopilotCliIdePackage()
	{
		// Resolve Microsoft.Terminal.Wpf from VS's Terminal extension folder.
		// VS registers it via ProvideCodeBase on its own package, but that doesn't
		// help third-party extensions. We resolve it from the known VS install path.
		AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
		{
			var name = new System.Reflection.AssemblyName(args.Name);
			if (!string.Equals(name.Name, "Microsoft.Terminal.Wpf", StringComparison.OrdinalIgnoreCase))
				return null;

			var devEnvDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
			if (devEnvDir == null)
				return null;

			var dllPath = Path.Combine(devEnvDir, "CommonExtensions", "Microsoft", "Terminal", "Microsoft.Terminal.Wpf.dll");
			return File.Exists(dllPath) ? System.Reflection.Assembly.LoadFrom(dllPath) : null;
		};
	}

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
	private TerminalSessionService? _terminalSession;

	protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
	{
		await base.InitializeAsync(cancellationToken, progress);
		await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

		try
		{
			_logger = OutputLogger.Create();
			VsServices.Instance.Logger = _logger;
			VsServices.Instance.OnResetNotificationState = ResetNotificationState;

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

			// Register Copilot CLI commands in the Tools menu
			if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
			{
				var cmdId = new CommandID(new Guid("e7a8b9c0-d1e2-4f3a-8b5c-6d7e8f9a0b1c"), 0x0100);
				commandService.AddCommand(new MenuCommand(OnLaunchCopilotCli, cmdId));

				var windowCmdId = new CommandID(new Guid("e7a8b9c0-d1e2-4f3a-8b5c-6d7e8f9a0b1c"), 0x0200);
				commandService.AddCommand(new MenuCommand(OnShowCopilotCliWindow, windowCmdId));
			}

			// Create terminal session service (survives tool window hide/show)
			_terminalSession = new TerminalSessionService(_logger);
			VsServices.Instance.TerminalSession = _terminalSession;
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
		var workspaceFolders = new List<string> { GetWorkspaceFolder() }.AsReadOnly();
		await _discovery!.WriteLockFileAsync(mcpPipeName, nonce, workspaceFolders);

		// Subscribe to Error List data layer for real-time diagnostic notifications
		var componentModel = (IComponentModel)GetGlobalService(typeof(SComponentModel));
		_diagnosticTracker = new DiagnosticTracker(
			componentModel,
			JoinableTaskFactory,
			() => _mcpCallbacks,
			cb => _mcpCallbacks = cb,
			_logger);
		_diagnosticTracker.Subscribe();
		VsServices.Instance.DiagnosticTracker = _diagnosticTracker;
	}

	// Tears down connection: removes lock file, kills server, disposes pipe.
	private void StopConnection()
	{
		_mcpCallbacks = null;

		_diagnosticTracker?.Dispose();
		_diagnosticTracker = null;
		VsServices.Instance.DiagnosticTracker = null;
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

	internal static string GetWorkspaceFolder()
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		try
		{
			var dte = (EnvDTE80.DTE2)GetGlobalService(typeof(EnvDTE.DTE));
			if (dte?.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
			{
				var dir = Path.GetDirectoryName(dte.Solution.FullName);
				if (!string.IsNullOrEmpty(dir))
					return dir;
			}
		}
		catch { /* Ignore */ }

		return Directory.GetCurrentDirectory();
	}

	private void OnSolutionOpened()
	{
		_ = JoinableTaskFactory.RunAsync(async () =>
		{
			try
			{
				await JoinableTaskFactory.SwitchToMainThreadAsync();
				await StartConnectionAsync();
				_terminalSession?.RestartSession(GetWorkspaceFolder());
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
				_terminalSession?.RestartSession(GetWorkspaceFolder());
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

	private void OnLaunchCopilotCli(object sender, EventArgs e)
	{
		// This assumes that copilot is on path
		ThreadHelper.ThrowIfNotOnUIThread();
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = "/k copilot",
				WorkingDirectory = GetWorkspaceFolder(),
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			_logger?.Log($"Failed to launch Copilot CLI (External Terminal): {ex.Message}");
		}
	}

	private void OnShowCopilotCliWindow(object sender, EventArgs e)
	{
		_ = JoinableTaskFactory.RunAsync(async () =>
		{
			try
			{
				await JoinableTaskFactory.SwitchToMainThreadAsync();
				var window = await ShowToolWindowAsync(typeof(TerminalToolWindow), 0, true, DisposalToken);
				if (window?.Frame is IVsWindowFrame frame)
					frame.Show();
			}
			catch (Exception ex)
			{
				_logger?.Log($"Failed to show Copilot CLI (Embedded Terminal): {ex.Message}");
			}
		});
	}

	private void OnBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action) => _diagnosticTracker?.SchedulePush();

	private void OnDocumentSaved(EnvDTE.Document document) => _diagnosticTracker?.SchedulePush();

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

			_terminalSession?.Dispose();
			_terminalSession = null;
			VsServices.Instance.TerminalSession = null;

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
