using CopilotCliIde.Shared;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace CopilotCliIde;

// Monitors the Error List data layer and pushes debounced, deduplicated diagnostic
// notifications to Copilot CLI. Also exposes CollectGrouped for on-demand reads
// (e.g. get_diagnostics tool calls). Data comes from sink-tracked snapshots — no UI dependency.
internal sealed class DiagnosticTracker : IDisposable
{
	private readonly IComponentModel _componentModel;
	private readonly Func<IMcpServerCallbacks?> _getCallbacks;
	private readonly Action<IMcpServerCallbacks?> _clearCallbacks;
	private readonly OutputLogger? _logger;
	private readonly JoinableTaskFactory _joinableTaskFactory;
	private readonly DebouncePusher _pusher;
	private readonly object _tableSubscriptionLock = new();
	private readonly HashSet<ITableDataSource> _subscribedSources = [];
	private readonly List<DiagnosticTableSink> _sinks = [];
	private readonly List<IDisposable> _tableSubscriptions = [];
	private ITableManager? _errorTableManager;

	public DiagnosticTracker(
		IComponentModel componentModel,
		JoinableTaskFactory joinableTaskFactory,
		Func<IMcpServerCallbacks?> getCallbacks,
		Action<IMcpServerCallbacks?> clearCallbacks,
		OutputLogger? logger = null)
	{
		_componentModel = componentModel;
		_joinableTaskFactory = joinableTaskFactory;
		_getCallbacks = getCallbacks;
		_clearCallbacks = clearCallbacks;
		_logger = logger;
		_pusher = new DebouncePusher(OnDebounceElapsed);
	}

	public void Subscribe()
	{
		try
		{
			var tableManagerProvider = _componentModel.GetService<ITableManagerProvider>();
			_errorTableManager = tableManagerProvider.GetTableManager(StandardTables.ErrorsTable);

			lock (_tableSubscriptionLock)
			{
				foreach (var source in _errorTableManager.Sources)
					SubscribeToSource(source);
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
			_sinks.Clear();
		}
	}

	public void SchedulePush()
	{
		if (_getCallbacks() == null) return;
		_pusher.Schedule();
	}

	public void ResetDedupKey() => _pusher.ResetDedupKey();

	// Reads all diagnostics from sink-tracked snapshots. Thread-safe, no UI dependency.
	public List<FileDiagnostics> CollectGrouped(string? filterFilePath = null, int maxItems = 200)
	{
		var fileGroups = new Dictionary<string, FileDiagnostics>(StringComparer.OrdinalIgnoreCase);
		List<DiagnosticTableSink> sinks;
		lock (_tableSubscriptionLock)
		{
			sinks = [.. _sinks];
		}

		var count = 0;
		foreach (var sink in sinks)
		{
			if (count >= maxItems) break;

			foreach (var snapshot in sink.GetCurrentSnapshots())
			{
				if (count >= maxItems) break;

				for (var i = 0; i < snapshot.Count; i++)
				{
					if (count >= maxItems) break;

					var fileName = TryGetString(snapshot, i, StandardTableKeyNames.DocumentName) ?? "";

					if (filterFilePath != null && !string.IsNullOrEmpty(fileName) &&
						!fileName.Equals(filterFilePath, StringComparison.OrdinalIgnoreCase))
						continue;

					if (!fileGroups.TryGetValue(fileName, out var group))
					{
						var fileUri = string.IsNullOrEmpty(fileName) ? "" : PathUtils.ToVsCodeFileUrl(fileName);
						group = new FileDiagnostics
						{
							Uri = fileUri,
							FilePath = PathUtils.ToLowerDriveLetter(fileName),
							Diagnostics = []
						};
						fileGroups[fileName] = group;
					}

					var line = TryGetInt(snapshot, i, StandardTableKeyNames.Line);
					var col = TryGetInt(snapshot, i, StandardTableKeyNames.Column);
					var endLine = line;
					var endCol = col;

					try
					{
						if (snapshot.TryGetValue(i, StandardTableKeyNames.PersistentSpan, out var spanObj)
							&& spanObj is ITrackingSpan trackingSpan)
						{
							var span = trackingSpan.GetSpan(trackingSpan.TextBuffer.CurrentSnapshot);
							var endLineSnap = span.End.GetContainingLine();
							endLine = endLineSnap.LineNumber;
							endCol = span.End.Position - endLineSnap.Start.Position;
						}
					}
					catch { /* Snapshot may have changed — fall back to end = start */ }

					var severity = DiagnosticSeverity.Information;
					if (snapshot.TryGetValue(i, StandardTableKeyNames.ErrorSeverity, out var sevObj)
						&& sevObj is __VSERRORCATEGORY cat)
					{
						severity = cat switch
						{
							__VSERRORCATEGORY.EC_ERROR => DiagnosticSeverity.Error,
							__VSERRORCATEGORY.EC_WARNING => DiagnosticSeverity.Warning,
							_ => DiagnosticSeverity.Information
						};
					}

					group.Diagnostics!.Add(new DiagnosticItem
					{
						Message = TryGetString(snapshot, i, StandardTableKeyNames.Text),
						Severity = severity,
						Range = new DiagnosticRange
						{
							Start = new SelectionPosition { Line = line, Character = col },
							End = new SelectionPosition { Line = endLine, Character = endCol }
						},
						Code = TryGetString(snapshot, i, StandardTableKeyNames.ErrorCode)
					});

					count++;
				}
			}
		}

		return [.. fileGroups.Values];
	}

	public void Dispose()
	{
		Unsubscribe();
		_pusher.Dispose();
	}

	private void SubscribeToSource(ITableDataSource source)
	{
		if (!_subscribedSources.Add(source)) return;

		var sink = new DiagnosticTableSink(SchedulePush);
		_sinks.Add(sink);
		var subscription = source.Subscribe(sink);
		_tableSubscriptions.Add(subscription);
	}

	private void OnErrorTableSourcesChanged(object sender, EventArgs e)
	{
		if (_errorTableManager == null) return;

		lock (_tableSubscriptionLock)
		{
			foreach (var source in _errorTableManager.Sources)
				SubscribeToSource(source);
		}
	}

	private void OnDebounceElapsed()
	{
		var callbacks = _getCallbacks();
		if (callbacks == null) return;

		_ = _joinableTaskFactory.RunAsync(async () =>
		{
			try
			{
				await _joinableTaskFactory.SwitchToMainThreadAsync();
				var files = CollectGrouped();
				var uris = files.Select(f => new DiagnosticsChangedUri
				{
					Uri = f.Uri,
					Diagnostics = f.Diagnostics
				}).ToList();

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

	private static string? TryGetString(ITableEntriesSnapshot snapshot, int index, string key)
		=> snapshot.TryGetValue(index, key, out var obj) && obj is string s ? s : null;

	private static int TryGetInt(ITableEntriesSnapshot snapshot, int index, string key)
		=> snapshot.TryGetValue(index, key, out var obj) && obj is int n ? Math.Max(0, n) : 0;
}
