using System.Diagnostics;
using System.Text.Json;

namespace PipeProxy;

/// <summary>
/// Discovers VS Code lock files in ~/.copilot/ide/ and manages the proxy's own lock file.
/// On dispose, deletes the proxy lock and restores the original VS Code lock file.
/// </summary>
sealed class LockFileManager : IDisposable
{
	private string? _proxyLockPath;
	private string? _originalLockPath;
	private string? _hiddenLockPath;

	private static string GetIdeDirectory() =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "ide");

	/// <summary>
	/// Scans ~/.copilot/ide/*.lock for a live VS Code instance (ideName contains "Code").
	/// </summary>
	public LockFileInfo? FindVsCodeLock()
	{
		var ideDir = GetIdeDirectory();
		if (!Directory.Exists(ideDir))
			return null;

		foreach (var file in Directory.GetFiles(ideDir, "*.lock"))
		{
			try
			{
				var json = File.ReadAllText(file);
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				if (!root.TryGetProperty("ideName", out var ideNameProp))
					continue;

				var ideName = ideNameProp.GetString();
				if (string.IsNullOrEmpty(ideName))
					continue;

				if (!root.TryGetProperty("pid", out var pidProp))
					continue;

				var pid = pidProp.GetInt32();
				if (!IsProcessAlive(pid))
					continue;

				var socketPath = root.GetProperty("socketPath").GetString()!;
				var pipePrefix = @"\\.\pipe\";
				var pipeName = socketPath.StartsWith(pipePrefix, StringComparison.OrdinalIgnoreCase)
					? socketPath[pipePrefix.Length..] : socketPath;

				string? authHeader = null;
				if (root.TryGetProperty("headers", out var headersProp) &&
					headersProp.TryGetProperty("Authorization", out var authProp))
				{
					authHeader = authProp.GetString();
				}

				var workspaceFolders = new List<string>();
				if (root.TryGetProperty("workspaceFolders", out var foldersProp) &&
					foldersProp.ValueKind == JsonValueKind.Array)
				{
					foreach (var f in foldersProp.EnumerateArray())
					{
						var folder = f.GetString();
						if (folder != null) workspaceFolders.Add(folder);
					}
				}

				var isTrusted = root.TryGetProperty("isTrusted", out var trustedProp) && trustedProp.GetBoolean();
				var scheme = root.TryGetProperty("scheme", out var schemeProp)
					? schemeProp.GetString() ?? "pipe" : "pipe";

				return new LockFileInfo(
					FilePath: file,
					SocketPath: socketPath,
					PipeName: pipeName,
					AuthHeader: authHeader ?? "",
					Pid: pid,
					IdeName: ideName,
					Scheme: scheme,
					WorkspaceFolders: workspaceFolders,
					IsTrusted: isTrusted
				);
			}
			catch { continue; }
		}

		return null;
	}

	/// <summary>
	/// Writes a proxy lock file copying fields from the original VS Code lock,
	/// but with the proxy's pipe path, PID, and a fresh timestamp.
	/// Hides the original lock so Copilot CLI discovers the proxy instead.
	/// </summary>
	public void WriteProxyLock(LockFileInfo original, string proxyPipeName)
	{
		var ideDir = GetIdeDirectory();
		Directory.CreateDirectory(ideDir);

		var id = Guid.NewGuid().ToString();
		_proxyLockPath = Path.Combine(ideDir, $"{id}.lock");

		var lockData = new
		{
			socketPath = $@"\\.\pipe\{proxyPipeName}",
			scheme = original.Scheme,
			headers = new Dictionary<string, string> { ["Authorization"] = original.AuthHeader },
			pid = Environment.ProcessId,
			ideName = original.IdeName,
			timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			workspaceFolders = original.WorkspaceFolders,
			isTrusted = original.IsTrusted,
		};

		var json = JsonSerializer.Serialize(lockData, new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(_proxyLockPath, json);

		// Hide original lock file so CLI picks our proxy
		_originalLockPath = original.FilePath;
		_hiddenLockPath = original.FilePath + ".proxy-hidden";
		try
		{
			File.Move(_originalLockPath, _hiddenLockPath);
		}
		catch
		{
			_hiddenLockPath = null;
		}
	}

	public void Dispose()
	{
		if (_proxyLockPath != null)
		{
			try { File.Delete(_proxyLockPath); } catch { /* Ignore */ }
			_proxyLockPath = null;
		}

		if (_hiddenLockPath != null && _originalLockPath != null)
		{
			try { File.Move(_hiddenLockPath, _originalLockPath); } catch { /* Ignore */ }
			_hiddenLockPath = null;
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
}

record LockFileInfo(
	string FilePath,
	string SocketPath,
	string PipeName,
	string AuthHeader,
	int Pid,
	string IdeName,
	string Scheme,
	List<string> WorkspaceFolders,
	bool IsTrusted
);
