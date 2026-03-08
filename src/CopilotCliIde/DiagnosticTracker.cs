using CopilotCliIde.Shared;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace CopilotCliIde;

/// <summary>
/// Monitors the Error List data layer for diagnostic changes and pushes
/// debounced, deduplicated notifications to Copilot CLI via a callback.
/// Uses ITableManagerProvider/ITableDataSink — the VS equivalent of
/// VS Code's <c>onDidChangeDiagnostics</c>.
/// </summary>
internal sealed class DiagnosticTracker : IDisposable
{
	private readonly IComponentModel _componentModel;
	private readonly Func<IMcpServerCallbacks?> _getCallbacks;
	private readonly Action<IMcpServerCallbacks?> _clearCallbacks;
	private readonly Func<List<DiagnosticsChangedUri>> _collectDiagnostics;
	private readonly OutputLogger? _logger;
	private readonly JoinableTaskFactory _joinableTaskFactory;
	private readonly DebouncePusher _pusher;
	private readonly object _tableSubscriptionLock = new();
	private readonly HashSet<ITableDataSource> _subscribedSources = [];
	private readonly List<IDisposable> _tableSubscriptions = [];
	private ITableManager? _errorTableManager;

	public DiagnosticTracker(
		IComponentModel componentModel,
		JoinableTaskFactory joinableTaskFactory,
		Func<IMcpServerCallbacks?> getCallbacks,
		Action<IMcpServerCallbacks?> clearCallbacks,
		Func<List<DiagnosticsChangedUri>> collectDiagnostics,
		OutputLogger? logger = null)
	{
		_componentModel = componentModel;
		_joinableTaskFactory = joinableTaskFactory;
		_getCallbacks = getCallbacks;
		_clearCallbacks = clearCallbacks;
		_collectDiagnostics = collectDiagnostics;
		_logger = logger;
		_pusher = new DebouncePusher(OnDebounceElapsed);
	}

	/// <summary>
	/// Subscribes to the Error List's underlying data layer so we get notified
	/// whenever any <see cref="ITableDataSource"/> (Roslyn, analyzers, build, etc.)
	/// pushes new diagnostics. Each source gets its own <see cref="DiagnosticTableSink"/>
	/// that funnels change notifications into <see cref="SchedulePush"/>.
	/// </summary>
	public void Subscribe()
	{
		try
		{
			var tableManagerProvider = _componentModel.GetService<ITableManagerProvider>();
			_errorTableManager = tableManagerProvider.GetTableManager(StandardTables.ErrorsTable);

			lock (_tableSubscriptionLock)
			{
				foreach (var source in _errorTableManager.Sources)
				{
					SubscribeToSource(source);
				}
			}

			_errorTableManager.SourcesChanged += OnErrorTableSourcesChanged;
		}
		catch (Exception ex)
		{
			_logger?.Log($"Failed to subscribe to Error List table: {ex.Message}");
		}
	}

	/// <summary>
	/// Stops monitoring, disposes all source subscriptions, and clears tracking state.
	/// </summary>
	public void Unsubscribe()
	{
		_errorTableManager?.SourcesChanged -= OnErrorTableSourcesChanged;
		_errorTableManager = null;

		lock (_tableSubscriptionLock)
		{
			foreach (var sub in _tableSubscriptions)
			{
				try { sub.Dispose(); }
				catch { /* Ignore */ }
			}
			_tableSubscriptions.Clear();
			_subscribedSources.Clear();
		}
	}

	/// <summary>
	/// Triggers a debounced diagnostics push (200ms, matching VS Code).
	/// Called by build/save event handlers and <see cref="DiagnosticTableSink"/>.
	/// </summary>
	public void SchedulePush()
	{
		if (_getCallbacks() == null) return;
		_pusher.Schedule();
	}

	/// <summary>
	/// Clears the dedup key so the next event is always sent, even if
	/// the content hasn't changed. Called when a new CLI client connects.
	/// </summary>
	public void ResetDedupKey() => _pusher.ResetDedupKey();

	/// <summary>
	/// Clears pending state and resets the debounce timer.
	/// Called on connection stop so the next connection starts fresh.
	/// </summary>
	public void Reset() => _pusher.Reset();

	public void Dispose()
	{
		Unsubscribe();
		_pusher.Dispose();
	}

	private void SubscribeToSource(ITableDataSource source)
	{
		if (!_subscribedSources.Add(source)) return;

		var subscription = source.Subscribe(new DiagnosticTableSink(SchedulePush));
		_tableSubscriptions.Add(subscription);
	}

	private void OnErrorTableSourcesChanged(object sender, EventArgs e)
	{
		if (_errorTableManager == null) return;

		lock (_tableSubscriptionLock)
		{
			foreach (var source in _errorTableManager.Sources)
			{
				SubscribeToSource(source);
			}
		}
	}

	/// <summary>
	/// Fires 200ms after the last diagnostics change trigger. Collects
	/// current Error List items on the UI thread and pushes them to the CLI.
	/// Deduplicates by comparing a fingerprint of the diagnostics content.
	/// </summary>
	private void OnDebounceElapsed()
	{
		var callbacks = _getCallbacks();
		if (callbacks == null) return;

		_ = _joinableTaskFactory.RunAsync(async () =>
		{
			try
			{
				await _joinableTaskFactory.SwitchToMainThreadAsync();
				var uris = _collectDiagnostics();

				var key = ComputeDiagnosticsKey(uris);
				if (_pusher.IsDuplicate(key)) return;

				var notification = new DiagnosticsChangedNotification { Uris = uris };
				var totalDiagnostics = uris.Sum(u => u.Diagnostics?.Count ?? 0);
				_logger?.Log($"Push diagnostics_changed: {uris.Count} file(s), {totalDiagnostics} diagnostic(s)");

				await Task.Run(async () =>
				{
					try { await callbacks.OnDiagnosticsChangedAsync(notification); }
					catch { _clearCallbacks(null); }
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
}
