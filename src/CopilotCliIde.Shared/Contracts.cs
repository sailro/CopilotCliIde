using System.Collections.Generic;
using System.Threading.Tasks;
using StreamJsonRpc;

namespace CopilotCliIde.Shared;

/// <summary>
/// RPC contract for VS services exposed to the MCP server process.
/// </summary>
[JsonRpcContract]
public partial interface IVsServiceRpc
{
	Task<DiffResult> OpenDiffAsync(string originalFilePath, string newFileContents, string tabName);
	Task<CloseDiffResult> CloseDiffByTabNameAsync(string tabName);
	Task<VsInfoResult> GetVsInfoAsync();
	Task<SelectionResult> GetSelectionAsync();
	Task<DiagnosticsResult> GetDiagnosticsAsync(string? uri);
	Task<ReadFileResult> ReadFileAsync(string filePath, int? startLine, int? maxLines);
	Task ResetNotificationStateAsync();
}

/// <summary>
/// Callback interface the VS extension calls on the MCP server process
/// to push real-time notifications (e.g., selection changes).
/// </summary>
[JsonRpcContract]
public partial interface IMcpServerCallbacks
{
	Task OnSelectionChangedAsync(SelectionNotification notification);
	Task OnDiagnosticsChangedAsync(DiagnosticsChangedNotification notification);
}

public class SelectionNotification
{
	public string? Text { get; set; }
	public string? FilePath { get; set; }
	public string? FileUrl { get; set; }
	public SelectionRange? Selection { get; set; }
}

public class SelectionRange
{
	public SelectionPosition? Start { get; set; }
	public SelectionPosition? End { get; set; }
	public bool IsEmpty { get; set; }
}

public class SelectionPosition
{
	public int Line { get; set; }
	public int Character { get; set; }
}

public class DiffResult
{
	public bool Success { get; set; }
	public string? DiffId { get; set; }
	public string? Error { get; set; }
	public string? OriginalFilePath { get; set; }
	public string? ProposedFilePath { get; set; }
	public string? TabName { get; set; }
	public string? Message { get; set; }
	public string? UserAction { get; set; }
	/// <summary>
	/// Resolution status aligned with VS Code: "SAVED" or "REJECTED".
	/// </summary>
	public string? Result { get; set; }
	/// <summary>
	/// What triggered the resolution (e.g. "accepted_via_button", "rejected_via_button", "closed_via_tab", "timeout").
	/// </summary>
	public string? Trigger { get; set; }
}

public class CloseDiffResult
{
	public bool Success { get; set; }
	public bool AlreadyClosed { get; set; }
	public string? Error { get; set; }
	public string? TabName { get; set; }
	public string? OriginalFilePath { get; set; }
	public string? Message { get; set; }
}

public class VsInfoResult
{
	public string? IdeName { get; set; }
	public string? AppName { get; set; }
	public string? Version { get; set; }
	public string? SolutionPath { get; set; }
	public string? SolutionName { get; set; }
	public string? SolutionDirectory { get; set; }
	public List<ProjectInfo>? Projects { get; set; }
	public int ProcessId { get; set; }
}

public class ProjectInfo
{
	public string? Name { get; set; }
	public string? FullName { get; set; }
}

public class SelectionResult
{
	public bool Current { get; set; }
	public string? FilePath { get; set; }
	public string? FileUrl { get; set; }
	public string? Text { get; set; }
	public SelectionRange? Selection { get; set; }
}

public class DiagnosticsResult
{
	public List<FileDiagnostics>? Files { get; set; }
	public string? Error { get; set; }
}

public class FileDiagnostics
{
	public string? Uri { get; set; }
	public string? FilePath { get; set; }
	public List<DiagnosticItem>? Diagnostics { get; set; }
}

public class DiagnosticItem
{
	public string? Message { get; set; }
	public string? Severity { get; set; }
	public DiagnosticRange? Range { get; set; }
	public string? Source { get; set; }
	public string? Code { get; set; }
}

public class DiagnosticRange
{
	public SelectionPosition? Start { get; set; }
	public SelectionPosition? End { get; set; }
}

public class ReadFileResult
{
	public string? FilePath { get; set; }
	public string? Content { get; set; }
	public string? Error { get; set; }
	public int TotalLines { get; set; }
	public int StartLine { get; set; }
	public int LinesReturned { get; set; }
}

public class DiagnosticsChangedNotification
{
	public List<DiagnosticsChangedUri>? Uris { get; set; }
}

public class DiagnosticsChangedUri
{
	public string? Uri { get; set; }
	public List<DiagnosticItem>? Diagnostics { get; set; }
}
