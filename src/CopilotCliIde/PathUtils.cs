namespace CopilotCliIde;

/// <summary>
/// Path helpers that produce URIs and casing matching the VS Code /ide protocol.
/// </summary>
/// <remarks>
/// <see cref="System.Uri"/> produces <c>file:///C:/Dev/file.cs</c> (uppercase drive, literal colon),
/// but the VS Code /ide protocol expects <c>file:///c%3A/Dev/file.cs</c> (lowercase drive,
/// percent-encoded colon). No BCL class performs this transformation, so these helpers exist
/// to bridge the gap.
/// </remarks>
internal static class PathUtils
{
	/// <summary>
	/// Converts an absolute Windows file path to a VS Code-compatible <c>file:///</c> URI
	/// with a lowercase drive letter and percent-encoded colon.
	/// </summary>
	/// <remarks>
	/// <see cref="System.Uri"/> and <see cref="System.Uri.AbsoluteUri"/> produce
	/// <c>file:///C:/Dev/file.cs</c> — uppercase drive letter and a literal colon.
	/// The VS Code /ide protocol requires <c>file:///c%3A/Dev/file.cs</c> instead.
	/// No BCL class handles this, so the method lowercases the drive letter and
	/// encodes the colon manually.
	/// </remarks>
	/// <example><c>C:\Dev\file.cs</c> → <c>file:///c%3A/Dev/file.cs</c></example>
	public static string ToVsCodeFileUrl(string filePath)
	{
		var path = filePath.Replace('\\', '/');
		if (path.Length >= 2 && path[1] == ':')
			path = char.ToLowerInvariant(path[0]) + "%3A" + path.Substring(2);
		return "file:///" + path;
	}

	/// <summary>
	/// Lowercases the drive letter of a Windows file path (e.g. <c>C:\Dev</c> → <c>c:\Dev</c>).
	/// </summary>
	/// <remarks>
	/// VS Code normalizes all workspace-folder paths to a lowercase drive letter.
	/// The VS DTE and <see cref="System.IO.Path"/> APIs return uppercase drive letters,
	/// so this method ensures lock-file paths match what Copilot CLI expects during
	/// workspace-folder comparison.
	/// </remarks>
	public static string ToLowerDriveLetter(string filePath)
	{
		if (filePath.Length >= 2 && filePath[1] == ':' && char.IsUpper(filePath[0]))
			return char.ToLowerInvariant(filePath[0]) + filePath.Substring(1);
		return filePath;
	}
}
