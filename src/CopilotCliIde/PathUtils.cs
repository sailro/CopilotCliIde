namespace CopilotCliIde;

// System.Uri produces file:///C:/path (uppercase drive, literal colon), but VS Code's
// /ide protocol expects file:///c%3A/path (lowercase, percent-encoded). These helpers bridge the gap.
internal static class PathUtils
{
	// C:\Dev\file.cs → file:///c%3A/Dev/file.cs (lowercase drive, percent-encoded colon)
	public static string ToVsCodeFileUrl(string filePath)
	{
		var path = filePath.Replace('\\', '/');
		if (path.Length >= 2 && path[1] == ':')
			path = char.ToLowerInvariant(path[0]) + "%3A" + path.Substring(2);
		return "file:///" + path;
	}

	// file:///c%3A/Dev/file.cs → c:\Dev\file.cs; non-URI strings pass through unchanged.
	public static string? NormalizeFileUri(string? uri)
	{
		if (string.IsNullOrEmpty(uri))
			return null;

		if (!uri!.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
			return uri;

		// Strip the file:/// prefix and percent-decode the remainder.
		var path = Uri.UnescapeDataString(uri.Substring(8));
		return path.Replace('/', '\\');
	}

	// VS Code normalizes workspace paths to lowercase drive letters; this matches that.
	public static string ToLowerDriveLetter(string filePath)
	{
		if (filePath.Length >= 2 && filePath[1] == ':' && char.IsUpper(filePath[0]))
			return char.ToLowerInvariant(filePath[0]) + filePath.Substring(1);
		return filePath;
	}
}
