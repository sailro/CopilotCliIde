namespace CopilotCliIde;

using System;

// Single point of contact between the VS package and VsServiceRpc (which is
// instantiated by StreamJsonRpc and can't use constructor injection).
internal sealed class VsServices
{
	public static VsServices Instance { get; } = new();

	public OutputLogger? Logger { get; set; }
	public Action? OnResetNotificationState { get; set; }
	public DiagnosticTracker? DiagnosticTracker { get; set; }
	public TerminalSessionService? TerminalSession { get; set; }

	// Toolbar handlers — set by the package, invoked by the WPF toolbar buttons in
	// the embedded terminal tool window. Null if the package isn't initialized yet.
	public Action? OnViewSessionHistory { get; set; }
	public Action? OnNewSession { get; set; }
	public Action? OnDeleteCurrentSession { get; set; }
}
