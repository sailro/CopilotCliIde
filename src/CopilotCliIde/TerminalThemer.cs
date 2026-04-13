using System.Drawing;
using Microsoft.Terminal.Wpf;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace CopilotCliIde;

// Creates a TerminalTheme from VS colors, following VS's own TerminalThemer.cs pattern.
// Color table values are in COLORREF (BGR) format — Windows Terminal convention.
internal static class TerminalThemer
{
	private static readonly TerminalTheme _darkTheme = new()
	{
		ColorTable =
		[
			0x0,        // Black
			0x3131cd,   // Red
			0x79bc0d,   // Green
			0x10e5e5,   // Yellow
			0xc87224,   // Blue
			0xbc3fbc,   // Magenta
			0xcda811,   // Cyan
			0xe5e5e5,   // White
			0x666666,   // Bright Black
			0x4c4cf1,   // Bright Red
			0x8bd123,   // Bright Green
			0x43f5f5,   // Bright Yellow
			0xea8e3b,   // Bright Blue
			0xd670d6,   // Bright Magenta
			0xdbb829,   // Bright Cyan
			0xe5e5e5,   // Bright White
		],
	};

	private static readonly TerminalTheme _lightTheme = new()
	{
		ColorTable =
		[
			0x0,        // Black
			0x3131cd,   // Red
			0x008000,   // Green
			0x007370,   // Yellow
			0xa55104,   // Blue
			0xbc05bc,   // Magenta
			0x977900,   // Cyan
			0x555555,   // White
			0x666666,   // Bright Black
			0x3131cd,   // Bright Red
			0x008000,   // Bright Green
			0x007370,   // Bright Yellow
			0xa55104,   // Bright Blue
			0xbc05bc,   // Bright Magenta
			0x977900,   // Bright Cyan
			0x555555,   // Bright White
		],
	};

	public static TerminalTheme GetTheme()
	{
		ThreadHelper.ThrowIfNotOnUIThread();

		var bgColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
		var isDark = IsDarkColor(bgColor);
		var theme = isDark ? _darkTheme : _lightTheme;

		theme.DefaultBackground = (uint)ColorTranslator.ToWin32(bgColor);
		theme.DefaultForeground = (uint)ColorTranslator.ToWin32(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey));
		theme.DefaultSelectionBackground = (uint)ColorTranslator.ToWin32(VSColorTheme.GetThemedColor(CommonControlsColors.ComboBoxTextInputSelectionColorKey));
		theme.CursorStyle = CursorStyle.BlinkingBlockDefault;

		return theme;
	}

	// Perceived brightness: dark if < 128 on a 0-255 scale (ITU-R BT.601 luma).
	private static bool IsDarkColor(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
}
