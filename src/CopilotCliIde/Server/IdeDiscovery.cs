using System.Diagnostics;
using System.Text.Json;

namespace CopilotCliIde.Server;

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
			isTrusted = true,
		};

		var json = JsonSerializer.Serialize(lockData, new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(_lockFilePath, json);
		return Task.CompletedTask;
	}

	public Task UpdateWorkspaceFoldersAsync(IReadOnlyList<string> workspaceFolders)
	{
		if (_lockFilePath == null || !File.Exists(_lockFilePath))
			return Task.CompletedTask;

		var json = File.ReadAllText(_lockFilePath);

		// Rewrite with updated workspaceFolders and timestamp
		var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
		dict["workspaceFolders"] = workspaceFolders;
		dict["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		var updated = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(_lockFilePath, updated);
		return Task.CompletedTask;
	}

	public Task CleanStaleFilesAsync()
	{
		var ideDir = GetIdeDirectory();
		if (!Directory.Exists(ideDir))
			return Task.CompletedTask;

		CleanStaleLockFiles(ideDir);
		CleanStaleLogFiles(ideDir);

		return Task.CompletedTask;
	}

	private static void CleanStaleLogFiles(string ideDir)
	{
		// Clean stale PID-based log files (vs-error-{pid}.log, vs-connection-{pid}.log)
		foreach (var file in Directory.GetFiles(ideDir, "vs-*.log"))
		{
			try
			{
				var name = Path.GetFileNameWithoutExtension(file);
				var lastDash = name.LastIndexOf('-');
				if (lastDash < 0) continue;
				if (!int.TryParse(name.Substring(lastDash + 1), out var pid)) continue;
				try
				{
					Process.GetProcessById(pid);
				}
				catch (ArgumentException)
				{
					try { File.Delete(file); } catch { /* Ignore */ }
				}
			}
			catch { /* Ignore */ }
		}
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
				try
				{
					Process.GetProcessById(pid);
					// Process still alive, skip
				}
				catch (ArgumentException)
				{
					// Process dead, remove stale lock file
					File.Delete(file);
				}
			}
			catch
			{
				// Malformed lock file, remove it
				try { File.Delete(file); } catch { /* Ignore */ }
			}
		}
	}

	public void RemoveLockFile()
	{
		if (_lockFilePath == null)
			return;

		try { File.Delete(_lockFilePath); } catch { /* Ignore */ }
		_lockFilePath = null;
	}

	public void Dispose() => RemoveLockFile();
}
