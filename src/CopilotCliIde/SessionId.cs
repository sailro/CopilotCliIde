namespace CopilotCliIde;

// Strict validation for Copilot CLI session IDs. The CLI uses lowercase UUIDs.
// Any value that fails this check must NOT be passed to the shell — the only
// path it currently flows through is cmd.exe interpolation in TerminalProcess.Start.
internal static class SessionId
{
	public static bool IsValid(string? id) =>
		!string.IsNullOrEmpty(id) && Guid.TryParseExact(id, "D", out _);
}
