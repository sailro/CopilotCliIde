using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace CopilotCliIde;

[Guid("d3e4f5a6-b7c8-4d9e-af01-2b3c4d5e6f7a")]
public sealed class TerminalToolWindow : ToolWindowPane
{
	public TerminalToolWindow() : base(null)
	{
		Caption = "Copilot CLI";
		Content = new TerminalToolWindowControl();
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && Content is TerminalToolWindowControl control)
			control.Dispose();

		base.Dispose(disposing);
	}
}
