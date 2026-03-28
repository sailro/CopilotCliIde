using System.Collections.Generic;
using System.Threading.Tasks;

namespace CopilotCliIde.Shared;

public interface IVsServiceRpc
{
	Task<DiffResult> OpenDiffAsync(string originalFilePath, string newFileContents, string tabName);
	Task<CloseDiffResult> CloseDiffByTabNameAsync(string tabName);
	Task<VsInfoResult> GetVsInfoAsync();
	Task<SelectionResult> GetSelectionAsync();
	Task<DiagnosticsResult> GetDiagnosticsAsync(string? uri);
	Task<ReadFileResult> ReadFileAsync(string filePath, int? startLine, int? maxLines);
	Task ResetNotificationStateAsync();
}

public interface IMcpServerCallbacks
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

public static class DiffOutcome
{
	public const string Saved = "SAVED";
	public const string Rejected = "REJECTED";
}

public static class DiffTrigger
{
	public const string AcceptedViaButton = "accepted_via_button";
	public const string RejectedViaButton = "rejected_via_button";
	public const string ClosedViaTool = "closed_via_tool";
}

public class DiffResult
{
	public bool Success { get; set; }
	public string? Error { get; set; }
	public string? TabName { get; set; }
	public string? Message { get; set; }
	// SAVED or REJECTED
	public string? Result { get; set; }
	// See DiffTrigger constants
	public string? Trigger { get; set; }
}

public class CloseDiffResult
{
	public bool Success { get; set; }
	public bool AlreadyClosed { get; set; }
	public string? Error { get; set; }
	public string? TabName { get; set; }
	public string? Message { get; set; }
}

public class VsInfoResult
{
	public string? Version { get; set; }
	public string? AppName { get; set; }
	public string? AppRoot { get; set; }
	public string? Language { get; set; }
	public string? MachineId { get; set; }
	public string? SessionId { get; set; }
	public string? UriScheme { get; set; }
	public string? Shell { get; set; }
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
	public string? Code { get; set; }
}

public static class DiagnosticSeverity
{
	public const string Error = "error";
	public const string Warning = "warning";
	public const string Information = "information";
}

public static class Notification
{
	public const string SelectionChanged = "selection_changed";
	public const string DiagnosticsChanged = "diagnostics_changed";
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
