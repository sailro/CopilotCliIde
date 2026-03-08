using Microsoft.VisualStudio.Shell.TableManager;

namespace CopilotCliIde;

/// <summary>
/// Lightweight <see cref="ITableDataSink"/> that triggers a debounced
/// diagnostics push whenever Roslyn (or any other Error List data source)
/// updates its snapshot. This is the data-layer equivalent of VS Code's
/// <c>onDidChangeDiagnostics</c>.
/// <para>
/// The sink does NOT read diagnostics — it only signals that something
/// changed. Actual collection happens via <see cref="ErrorListReader"/>.
/// </para>
/// </summary>
internal sealed class DiagnosticTableSink(Action onChanged) : ITableDataSink
{
	public bool IsStable
	{
		get => true;
		set => onChanged();
	}

	public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries)
		=> onChanged();

	public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries)
		=> onChanged();

	public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries)
		=> onChanged();

	public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories)
		=> onChanged();

	public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory)
		=> onChanged();

	public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory)
		=> onChanged();

	public void FactorySnapshotChanged(ITableEntriesSnapshotFactory factory)
		=> onChanged();

	public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots)
		=> onChanged();

	public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot)
		=> onChanged();

	public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot)
		=> onChanged();

	public void RemoveAllEntries()
		=> onChanged();

	public void RemoveAllSnapshots()
		=> onChanged();

	public void RemoveAllFactories()
		=> onChanged();
}
