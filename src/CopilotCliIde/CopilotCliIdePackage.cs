using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using CopilotCliIde.Shared;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using StreamJsonRpc;
using Task = System.Threading.Tasks.Task;

namespace CopilotCliIde;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideBindingPath]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(TerminalToolWindow), Transient = true, Style = VsDockStyle.Tabbed, Window = "34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3")]
[ProvideSettingsManifest]
[ProvideService(typeof(TerminalSettingsProvider), IsAsyncQueryable = true)]
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

			var basePath = Path.Combine(devEnvDir, "CommonExtensions", "Microsoft", "Terminal");
			var dllPath = Path.Combine(basePath, "Microsoft.Terminal.Wpf.dll");
			if (!File.Exists(dllPath))
				dllPath = Path.Combine(basePath, "Terminal.Wpf", "Microsoft.Terminal.Wpf.dll");
			if (!File.Exists(dllPath))
			{
				VsServices.Instance?.Logger?.Log($"Microsoft.Terminal.Wpf.dll not found in {basePath}");
				return null;
			}
			return System.Reflection.Assembly.LoadFrom(dllPath);
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
	private VsServiceRpc? _vsServiceRpc;
	private DiagnosticTracker? _diagnosticTracker;
	private OutputLogger? _logger;
	private TerminalSessionService? _terminalSession;

	protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
	{
		AddService(typeof(TerminalSettingsProvider), (_, _, _) =>
		{
			var settingsManager = new ShellSettingsManager(this);
			var store = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);
			return Task.FromResult<object?>(new TerminalSettingsProvider(store));
		}, true);

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

			// Register Copilot CLI commands in the Tools menu and tool window toolbar
			if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
			{
				var cmdSetGuid = new Guid("e7a8b9c0-d1e2-4f3a-8b5c-6d7e8f9a0b1c");

				commandService.AddCommand(new MenuCommand(OnLaunchCopilotCli, new CommandID(cmdSetGuid, 0x0100)));
				commandService.AddCommand(new MenuCommand(OnShowCopilotCliWindow, new CommandID(cmdSetGuid, 0x0200)));

				// Toolbar buttons on the embedded terminal tool window
				var viewHistoryCmd = new OleMenuCommand(OnViewSessionHistory, new CommandID(cmdSetGuid, 0x0300));
				viewHistoryCmd.BeforeQueryStatus += OnQueryWorkspaceCommandStatus;
				commandService.AddCommand(viewHistoryCmd);

				var newSessionCmd = new OleMenuCommand(OnNewSession, new CommandID(cmdSetGuid, 0x0301));
				newSessionCmd.BeforeQueryStatus += OnQueryWorkspaceCommandStatus;
				commandService.AddCommand(newSessionCmd);
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

	// Tears down connection: cleans up diffs, removes lock file, kills server, disposes pipe.
	private void StopConnection()
	{
		ThreadHelper.ThrowIfNotOnUIThread();

		_mcpCallbacks = null;

		_vsServiceRpc?.CleanupAllDiffs();
		_vsServiceRpc = null;

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
			_vsServiceRpc = new VsServiceRpc();
			_rpc = JsonRpc.Attach(_rpcPipe, _vsServiceRpc);
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
				// New solution context — drop any prior resume id so we start fresh.
				_terminalSession?.RestartFresh(GetWorkspaceFolder());
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
				_terminalSession?.RestartFresh(GetWorkspaceFolder());
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
				{
					frame.Show();

					// Yield to let VS finish its frame activation and focus handling,
					// then switch back to UI thread to focus the native terminal control.
					// Without this, VS steals focus back during its own activation sequence.
					// Pattern from VS's own TerminalWindowBase.OnActiveFrameChanged.
					await Task.Run(() => { });
					await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
					(window.Content as TerminalToolWindowControl)?.FocusTerminal();
				}
			}
			catch (Exception ex)
			{
				_logger?.Log($"Failed to show Copilot CLI (Embedded Terminal): {ex.Message}");
			}
		});
	}

	private void OnBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action) => _diagnosticTracker?.SchedulePush();

	private void OnDocumentSaved(EnvDTE.Document document) => _diagnosticTracker?.SchedulePush();

	private void OnQueryWorkspaceCommandStatus(object sender, EventArgs e)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		if (sender is not OleMenuCommand cmd)
			return;
		// Both toolbar commands require an active terminal session service AND a real
		// open solution. Without a solution, GetWorkspaceFolder() falls back to the
		// process's CWD which is rarely meaningful for session matching.
		cmd.Enabled = _terminalSession != null && IsSolutionOpen();
	}

	private static bool IsSolutionOpen()
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		try
		{
			var dte = (EnvDTE80.DTE2)GetGlobalService(typeof(EnvDTE.DTE));
			return dte?.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName);
		}
		catch
		{
			return false;
		}
	}

	private void OnNewSession(object sender, EventArgs e)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		try
		{
			if (_terminalSession == null)
				return;
			if (!ConfirmReplaceRunningSession("Start a new Copilot CLI session?"))
				return;

			_terminalSession.RestartFresh(GetWorkspaceFolder());
		}
		catch (Exception ex)
		{
			_logger?.Log($"OnNewSession failed: {ex.Message}");
		}
	}

	private void OnViewSessionHistory(object sender, EventArgs e)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		try
		{
			if (_terminalSession == null)
				return;

			var workspace = GetWorkspaceFolder();
			var dialog = new SessionPickerDialog(new SessionStore(_logger), workspace);
			var ok = dialog.ShowModal() == true;
			if (!ok || dialog.SelectedSessionId == null)
				return;

			if (!SessionId.IsValid(dialog.SelectedSessionId))
			{
				_logger?.Log("OnViewSessionHistory: rejected invalid session id from picker");
				return;
			}

			if (!ConfirmReplaceRunningSession("Resume the selected Copilot CLI session?"))
				return;

			_terminalSession.RestartResuming(workspace, dialog.SelectedSessionId);
			ShowToolWindowFireAndForget();
		}
		catch (Exception ex)
		{
			_logger?.Log($"OnViewSessionHistory failed: {ex.Message}");
		}
	}

	private bool ConfirmReplaceRunningSession(string question)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		if (_terminalSession?.IsRunning != true)
			return true;

		var result = VsShellUtilities.ShowMessageBox(
			this,
			$"The Copilot CLI is currently running. {question} Any unsent input will be discarded.",
			"Copilot CLI",
			Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_QUERY,
			Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
			Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
		return result == 1; // IDOK
	}

	private void ShowToolWindowFireAndForget()
	{
		_ = JoinableTaskFactory.RunAsync(async () =>
		{
			try
			{
				await JoinableTaskFactory.SwitchToMainThreadAsync();
				var window = await ShowToolWindowAsync(typeof(TerminalToolWindow), 0, true, DisposalToken);
				(window?.Frame as IVsWindowFrame)?.Show();
			}
			catch (Exception ex)
			{
				_logger?.Log($"ShowToolWindowFireAndForget failed: {ex.Message}");
			}
		});
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

#pragma warning disable VSTHRD010 // Accessing ... should only be done on the main thread — Dispose runs on UI thread
			StopConnection();
#pragma warning restore VSTHRD010

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
