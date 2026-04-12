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

	// Prevent VS command routing from intercepting keys meant for the terminal.
	// Arrow keys, Tab, Escape, etc. must reach the native TerminalControl.
	protected override bool PreProcessMessage(ref System.Windows.Forms.Message m)
	{
		const int WM_KEYDOWN = 0x0100;
		if (m.Msg == WM_KEYDOWN)
		{
			var key = (int)m.WParam & 0xFF;
			// Arrow keys (37-40), Tab (9), Escape (27), Enter (13),
			// Backspace (8), Delete (46), Home (36), End (35), PgUp (33), PgDn (34)
			if (key is >= 33 and <= 40 or 8 or 9 or 13 or 27 or 46)
				return false;
		}
		return base.PreProcessMessage(ref m);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && Content is TerminalToolWindowControl control)
			control.Dispose();

		base.Dispose(disposing);
	}
}
