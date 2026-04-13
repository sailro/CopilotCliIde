using System.Diagnostics;
using System.Reflection;

namespace CopilotCliIde;

public sealed class ServerProcessManager : IDisposable
{
	private Process? _process;

	public async Task StartAsync(string rpcPipeName, string mcpPipeName, string nonce)
	{
		var extensionDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
		var serverDir = Path.Combine(extensionDir, "McpServer");
		var serverDll = Path.Combine(serverDir, "CopilotCliIde.Server.dll");

		if (!File.Exists(serverDll))
			throw new FileNotFoundException($"MCP server not found: {serverDll}");

		_process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				Arguments = $"\"{serverDll}\" --rpc-pipe {rpcPipeName} --mcp-pipe {mcpPipeName} --nonce {nonce}",
				WorkingDirectory = serverDir,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			},
			EnableRaisingEvents = true
		};

		var ready = new TaskCompletionSource<bool>();
		_process.OutputDataReceived += (s, e) =>
		{
			if (e.Data == "READY") ready.TrySetResult(true);
		};
		_process.Exited += (s, e) => ready.TrySetResult(false);

		_process.Start();
		_process.BeginOutputReadLine();

		if (await Task.WhenAny(ready.Task, Task.Delay(10_000)) != ready.Task)
			throw new TimeoutException("MCP server did not become ready within 10 seconds");

		if (!await ready.Task)
			throw new Exception($"MCP server exited with code {_process.ExitCode}");
	}

	public void Dispose()
	{
		if (_process is { HasExited: false })
		{
			try
			{
				_process.StandardInput.Close();
				_process.WaitForExit(3000);
				if (!_process.HasExited) _process.Kill();
			}
			catch { /* Ignore */ }
		}
		_process?.Dispose();
		_process = null;
	}
}
