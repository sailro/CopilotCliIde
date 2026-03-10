using CopilotCliIde.Shared;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;

namespace CopilotCliIde;

/// <summary>
/// Reads Error List items and returns them grouped by file as
/// <see cref="FileDiagnostics"/> DTOs. Shared between the on-demand RPC
/// path (<see cref="VsServiceRpc.GetDiagnosticsAsync"/>) and the push
/// notification path (<see cref="CopilotCliIdePackage"/>).
/// <para>
/// Prefers the modern <see cref="IErrorList"/> table control API which
/// exposes error codes (<c>StandardTableKeyNames.ErrorCode</c>) and
/// 0-based positions. Falls back to DTE <c>ErrorItems</c> when the
/// table control is unavailable (e.g., Error List window never opened).
/// </para>
/// <para>
/// <b>End position:</b> When available, the table API's
/// <c>StandardTableKeyNames.PersistentSpan</c> provides an
/// <see cref="ITrackingSpan"/> with the full diagnostic range (start
/// and end). The DTE fallback path still sets end equal to start.
/// </para>
/// </summary>
internal static class ErrorListReader
{
	/// <summary>
	/// Collects Error List items grouped by file. Must be called on the UI thread.
	/// </summary>
	/// <param name="filterFilePath">
	/// Optional file path filter. When non-null, only diagnostics whose
	/// filename matches (case-insensitive) are included.
	/// </param>
	/// <param name="maxItems">Maximum number of Error List items to read.</param>
	internal static List<FileDiagnostics> CollectGrouped(string? filterFilePath = null, int maxItems = 200)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		var fileGroups = new Dictionary<string, FileDiagnostics>(StringComparer.OrdinalIgnoreCase);
		try
		{
			// Prefer the modern table-backed Error List API for richer data
			// (error codes via StandardTableKeyNames.ErrorCode, 0-based positions).
			if (!TryCollectFromTableControl(fileGroups, filterFilePath, maxItems))
			{
				// Fall back to DTE ErrorItems when the table control isn't available.
				CollectFromDte(fileGroups, filterFilePath, maxItems);
			}
		}
		catch { /* Ignore — DTE/table can throw during shutdown */ }
		return [.. fileGroups.Values];
	}

	/// <summary>
	/// Reads Error List entries via <see cref="IErrorList.TableControl"/>.
	/// Returns false if the service or table control is unavailable.
	/// </summary>
	private static bool TryCollectFromTableControl(
		Dictionary<string, FileDiagnostics> fileGroups, string? filterFilePath, int maxItems)
	{
		ThreadHelper.ThrowIfNotOnUIThread();

		if (Package.GetGlobalService(typeof(SVsErrorList)) is not IErrorList errorList)
			return false;

		var entries = errorList.TableControl?.Entries;
		if (entries == null)
			return false;

		var count = 0;
		foreach (var entry in entries)
		{
			if (count >= maxItems) break;

			var fileName = TryGetString(entry, StandardTableKeyNames.DocumentName) ?? "";

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

			var line = TryGetInt(entry, StandardTableKeyNames.Line);
			var col = TryGetInt(entry, StandardTableKeyNames.Column);
			var endLine = line;
			var endCol = col;

			try
			{
				if (entry.TryGetValue(StandardTableKeyNames.PersistentSpan, out var spanObj)
					&& spanObj is ITrackingSpan trackingSpan)
				{
					var span = trackingSpan.GetSpan(trackingSpan.TextBuffer.CurrentSnapshot);
					var endLineSnap = span.End.GetContainingLine();
					endLine = endLineSnap.LineNumber;
					endCol = span.End.Position - endLineSnap.Start.Position;
				}
			}
			catch { /* Snapshot may have changed — fall back to end = start */ }

			var severity = "information";
			if (entry.TryGetValue(StandardTableKeyNames.ErrorSeverity, out var sevObj) && sevObj is __VSERRORCATEGORY cat)
			{
				severity = cat switch
				{
					__VSERRORCATEGORY.EC_ERROR => "error",
					__VSERRORCATEGORY.EC_WARNING => "warning",
					_ => "information"
				};
			}

			group.Diagnostics!.Add(new DiagnosticItem
			{
				Message = TryGetString(entry, StandardTableKeyNames.Text),
				Severity = severity,
				Range = new DiagnosticRange
				{
					Start = new SelectionPosition { Line = line, Character = col },
					End = new SelectionPosition { Line = endLine, Character = endCol }
				},
				Code = TryGetString(entry, StandardTableKeyNames.ErrorCode)
			});

			count++;
		}

		return true;
	}

	/// <summary>
	/// Reads Error List entries via DTE <c>ErrorItems</c>. Used as a fallback
	/// when the table control is unavailable. Does not populate <c>Code</c>
	/// because the DTE <c>ErrorItem</c> interface does not expose error codes.
	/// </summary>
	private static void CollectFromDte(
		Dictionary<string, FileDiagnostics> fileGroups, string? filterFilePath, int maxItems)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
		var errorItems = dte?.ToolWindows.ErrorList.ErrorItems;
		if (errorItems == null) return;

		for (var i = 1; i <= Math.Min(errorItems.Count, maxItems); i++)
		{
			var item = errorItems.Item(i);
			var itemFile = item.FileName ?? "";

			if (filterFilePath != null && !string.IsNullOrEmpty(itemFile) &&
				!itemFile.Equals(filterFilePath, StringComparison.OrdinalIgnoreCase))
				continue;

			if (!fileGroups.TryGetValue(itemFile, out var group))
			{
				var fileUri = string.IsNullOrEmpty(itemFile) ? "" : PathUtils.ToVsCodeFileUrl(itemFile);
				group = new FileDiagnostics
				{
					Uri = fileUri,
					FilePath = PathUtils.ToLowerDriveLetter(itemFile),
					Diagnostics = []
				};
				fileGroups[itemFile] = group;
			}

			var line = Math.Max(0, item.Line - 1);
			var col = Math.Max(0, item.Column - 1);
			group.Diagnostics!.Add(new DiagnosticItem
			{
				Message = item.Description,
				Severity = item.ErrorLevel.ToProtocolSeverity(),
				Range = new DiagnosticRange
				{
					Start = new SelectionPosition { Line = line, Character = col },
					End = new SelectionPosition { Line = line, Character = col }
				},
			});
		}
	}

	private static string? TryGetString(ITableEntryHandle entry, string key)
		=> entry.TryGetValue(key, out var obj) && obj is string s ? s : null;

	private static int TryGetInt(ITableEntryHandle entry, string key)
		=> entry.TryGetValue(key, out var obj) && obj is int n ? Math.Max(0, n) : 0;
}
