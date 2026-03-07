namespace CopilotCliIde;

internal static class PathUtils
{
	/// <summary>
	/// Formats a file path as a VS Code-compatible file URI (lowercase drive, encoded colon).
	/// e.g. C:\Dev\file.cs → file:///c%3A/Dev/file.cs
	/// </summary>
	public static string ToVsCodeFileUrl(string filePath)
	{
		var path = filePath.Replace('\\', '/');
		if (path.Length >= 2 && path[1] == ':')
			path = char.ToLowerInvariant(path[0]) + "%3A" + path.Substring(2);
		return "file:///" + path;
	}

	public static string ToLowerDriveLetter(string filePath)
	{
		if (filePath.Length >= 2 && filePath[1] == ':' && char.IsUpper(filePath[0]))
			return char.ToLowerInvariant(filePath[0]) + filePath.Substring(1);
		return filePath;
	}
}
