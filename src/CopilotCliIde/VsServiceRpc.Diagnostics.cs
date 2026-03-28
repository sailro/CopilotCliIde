using CopilotCliIde.Shared;
using Microsoft.VisualStudio.Shell;

namespace CopilotCliIde;

public partial class VsServiceRpc
{
	public async Task<DiagnosticsResult> GetDiagnosticsAsync(string? uri)
	{
		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
		try
		{
			var filePath = PathUtils.NormalizeFileUri(uri);
			var files = VsServices.Instance.DiagnosticTracker?.CollectGrouped(filePath, maxItems: 100) ?? [];
			var totalDiagnostics = files.Sum(f => f.Diagnostics?.Count ?? 0);
			var scope = filePath != null ? Path.GetFileName(filePath) : "(all)";
			VsServices.Instance.Logger?.Log($"Tool get_diagnostics: {scope} {files.Count} file(s), {totalDiagnostics} diagnostic(s)");
			return new DiagnosticsResult { Files = files };
		}
		catch (Exception ex)
		{
			VsServices.Instance.Logger?.Log($"Tool get_diagnostics: error: {ex.Message}");
			return new DiagnosticsResult { Error = ex.Message };
		}
	}
}
