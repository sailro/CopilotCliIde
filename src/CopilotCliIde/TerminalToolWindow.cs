using System;
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

	// Escape key sequence matching VS's TerminalWindowBase.EscKeyCode (Kitty keyboard protocol).
	private const string EscKeySequence = "\u001b[27;1;27;1;0;1_";

	// Prevent VS command routing from intercepting keys meant for the terminal.
	// Arrow keys, Tab, etc. return false (normal dispatch reaches the TerminalContainer HWND).
	// Escape must be handled specially: VS maps Escape to "deactivate tool window",
	// so we forward the escape sequence to the terminal session and return true to consume it.
	protected override bool PreProcessMessage(ref System.Windows.Forms.Message m)
	{
		const int WM_KEYDOWN = 0x0100;
		if (m.Msg == WM_KEYDOWN)
		{
			var key = (int)m.WParam & 0xFF;
			if (key == 27 && System.Windows.Forms.Control.ModifierKeys == System.Windows.Forms.Keys.None)
			{
				if (Content is TerminalToolWindowControl control)
					control.SendInput(EscKeySequence);
				return true;
			}
			// Arrow keys (37-40), Tab (9), Enter (13),
			// Backspace (8), Delete (46), Home (36), End (35), PgUp (33), PgDn (34)
			if (key is >= 33 and <= 40 or 8 or 9 or 13 or 46)
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
