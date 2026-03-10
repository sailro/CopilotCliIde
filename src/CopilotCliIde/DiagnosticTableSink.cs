using Microsoft.VisualStudio.Shell.TableManager;

namespace CopilotCliIde;

// Tracks snapshot factories and direct snapshots from an Error List data source.
// Each source gets its own sink instance. Call GetCurrentSnapshots() to read entries.
internal sealed class DiagnosticTableSink(Action onChanged) : ITableDataSink
{
	private readonly object _lock = new();
	private readonly List<ITableEntriesSnapshotFactory> _factories = [];
	private readonly List<ITableEntriesSnapshot> _directSnapshots = [];

	public List<ITableEntriesSnapshot> GetCurrentSnapshots()
	{
		lock (_lock)
		{
			var result = new List<ITableEntriesSnapshot>();

			// Snapshots from factories (the common path for Roslyn, build errors, etc.)
			foreach (var factory in _factories)
			{
				try
				{
					var snapshot = factory.GetCurrentSnapshot();
					if (snapshot != null)
						result.Add(snapshot);
				}
				catch { /* factory may be stale */ }
			}

			// Snapshots delivered directly (some sources skip factories)
			result.AddRange(_directSnapshots);

			return result;
		}
	}

	public bool IsStable
	{
		get => true;
		set { _ = value; onChanged(); }
	}

	public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories)
	{
		lock (_lock)
		{
			if (removeAllFactories) _factories.Clear();
			_factories.Add(newFactory);
		}
		onChanged();
	}

	public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory)
	{
		lock (_lock) { _factories.Remove(oldFactory); }
		onChanged();
	}

	public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory)
	{
		lock (_lock)
		{
			_factories.Remove(oldFactory);
			_factories.Add(newFactory);
		}
		onChanged();
	}

	public void FactorySnapshotChanged(ITableEntriesSnapshotFactory factory)
		=> onChanged();

	public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries)
		=> onChanged();

	public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries)
		=> onChanged();

	public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries)
		=> onChanged();

	public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots)
	{
		lock (_lock)
		{
			if (removeAllSnapshots) _directSnapshots.Clear();
			_directSnapshots.Add(newSnapshot);
		}
		onChanged();
	}

	public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot)
	{
		lock (_lock) { _directSnapshots.Remove(oldSnapshot); }
		onChanged();
	}

	public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot)
	{
		lock (_lock)
		{
			_directSnapshots.Remove(oldSnapshot);
			_directSnapshots.Add(newSnapshot);
		}
		onChanged();
	}

	public void RemoveAllEntries()
		=> onChanged();

	public void RemoveAllSnapshots()
	{
		lock (_lock) { _directSnapshots.Clear(); }
		onChanged();
	}

	public void RemoveAllFactories()
	{
		lock (_lock) { _factories.Clear(); }
		onChanged();
	}
}
