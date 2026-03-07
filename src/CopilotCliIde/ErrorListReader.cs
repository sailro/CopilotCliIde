using CopilotCliIde.Shared;
using Microsoft.VisualStudio.Shell;

namespace CopilotCliIde;

/// <summary>
/// Reads Error List items from DTE and returns them grouped by file as
/// <see cref="FileDiagnostics"/> DTOs. Shared between the on-demand RPC
/// path (<see cref="VsServiceRpc.GetDiagnosticsAsync"/>) and the push
/// notification path (<see cref="CopilotCliIdePackage"/>).
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
			var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
			var errorItems = dte?.ToolWindows.ErrorList.ErrorItems;
			if (errorItems != null)
			{
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
						Source = item.Project
					});
				}
			}
		}
		catch { /* Ignore — DTE can throw during shutdown */ }
		return [.. fileGroups.Values];
	}
}
