using CopilotCliIde.Shared;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace CopilotCliIde;

// Monitors the Error List data layer (ITableManagerProvider/ITableDataSink) and pushes
// debounced, deduplicated diagnostic notifications to Copilot CLI.
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

	// Subscribes to each ITableDataSource in the Error List so diagnostic changes funnel into SchedulePush.
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

	public void SchedulePush()
	{
		if (_getCallbacks() == null) return;
		_pusher.Schedule();
	}

	public void ResetDedupKey() => _pusher.ResetDedupKey();

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

	// Collects Error List items on UI thread, deduplicates by fingerprint, and pushes to CLI.
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
