using System.Diagnostics;
using System.Text.Json;

namespace CopilotCliIde;

/// <summary>
/// Manages lock files in ~/.copilot/ide/ so Copilot CLI can discover this VS instance.
/// </summary>
public sealed class IdeDiscovery : IDisposable
{
	private string? _lockFilePath;

	private static string GetIdeDirectory()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "ide");
	}

	public Task WriteLockFileAsync(string pipeName, string nonce, IReadOnlyList<string> workspaceFolders)
	{
		var ideDir = GetIdeDirectory();
		Directory.CreateDirectory(ideDir);

		var id = Guid.NewGuid().ToString();
		_lockFilePath = Path.Combine(ideDir, $"{id}.lock");

		var lockData = new
		{
			socketPath = $@"\\.\pipe\{pipeName}",
			scheme = "pipe",
			headers = new Dictionary<string, string> { ["Authorization"] = $"Nonce {nonce}" },
			pid = Process.GetCurrentProcess().Id,
			ideName = "Visual Studio",
			timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			workspaceFolders,
			isTrusted = true
		};

		var json = JsonSerializer.Serialize(lockData, new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(_lockFilePath, json);
		return Task.CompletedTask;
	}

	public Task CleanStaleFilesAsync()
	{
		var ideDir = GetIdeDirectory();
		if (!Directory.Exists(ideDir))
			return Task.CompletedTask;

		CleanStaleLockFiles(ideDir);

		return Task.CompletedTask;
	}

	private static void CleanStaleLockFiles(string ideDir)
	{
		var lockFiles = Directory.GetFiles(ideDir, "*.lock");
		foreach (var file in lockFiles)
		{
			try
			{
				var json = File.ReadAllText(file);
				using var doc = JsonDocument.Parse(json);

				if (!doc.RootElement.TryGetProperty("pid", out var pidProp))
					continue;

				var pid = pidProp.GetInt32();
				if (!IsProcessAlive(pid))
					SafeDelete(file);
			}
			catch
			{
				// Malformed lock file, remove it
				SafeDelete(file);
			}
		}
	}

	[DebuggerNonUserCode]
	private static bool IsProcessAlive(int pid)
	{
		try
		{
			Process.GetProcessById(pid);
			return true;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	[DebuggerNonUserCode]
	private static void SafeDelete(string path)
	{
		try { File.Delete(path); } catch { /* Ignore */ }
	}

	public void RemoveLockFile()
	{
		if (_lockFilePath == null)
			return;

		SafeDelete(_lockFilePath);
		_lockFilePath = null;
	}

	public void Dispose() => RemoveLockFile();
}
