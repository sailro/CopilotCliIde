using System.Text.RegularExpressions;

namespace CopilotCliIde;

// Strict validation for Copilot CLI session IDs. The CLI uses lowercase UUIDs,
// but we accept either case. Any value that fails this check must NOT be passed
// to the shell — the only path it currently flows through is cmd.exe interpolation
// in TerminalProcess.Start.
internal static class SessionId
{
	private static readonly Regex Pattern = new(
		"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static bool IsValid(string? id) => !string.IsNullOrEmpty(id) && Pattern.IsMatch(id);
}
