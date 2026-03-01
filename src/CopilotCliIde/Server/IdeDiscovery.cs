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
        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var basePath = !string.IsNullOrEmpty(xdgState)
            ? Path.Combine(xdgState, ".copilot", "ide")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "ide");
        return basePath;
    }

    public async Task WriteLockFileAsync(string pipeName, string nonce, IReadOnlyList<string> workspaceFolders)
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
            pid = Environment.ProcessId,
            ideName = "Visual Studio",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            workspaceFolders,
            isTrusted = true,
        };

        var json = JsonSerializer.Serialize(lockData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_lockFilePath, json);
    }

    public async Task UpdateWorkspaceFoldersAsync(IReadOnlyList<string> workspaceFolders)
    {
        if (_lockFilePath == null || !File.Exists(_lockFilePath))
            return;

        var json = await File.ReadAllTextAsync(_lockFilePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Rewrite with updated workspaceFolders and timestamp
        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
        dict["workspaceFolders"] = workspaceFolders;
        dict["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var updated = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_lockFilePath, updated);
    }

    public async Task CleanStaleLockFilesAsync()
    {
        var ideDir = GetIdeDirectory();
        if (!Directory.Exists(ideDir))
            return;

        var lockFiles = Directory.GetFiles(ideDir, "*.lock");
        foreach (var file in lockFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("pid", out var pidProp))
                {
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
            }
            catch
            {
                // Malformed lock file, remove it
                try { File.Delete(file); } catch { }
            }
        }
    }

    public void RemoveLockFile()
    {
        if (_lockFilePath != null)
        {
            try { File.Delete(_lockFilePath); } catch { }
            _lockFilePath = null;
        }
    }

    public void Dispose() => RemoveLockFile();
}
