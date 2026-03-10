namespace CopilotCliIde;

// Single point of contact between the VS package and VsServiceRpc (which is
// instantiated by StreamJsonRpc and can't use constructor injection).
internal sealed class VsServices
{
	public static VsServices Instance { get; } = new();

	public OutputLogger? Logger { get; set; }
	public Action? OnResetNotificationState { get; set; }
	public DiagnosticTracker? DiagnosticTracker { get; set; }
}
