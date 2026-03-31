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

		_process.Start();
		await Task.Delay(200); // Let server start

		if (_process.HasExited)
			throw new Exception($"MCP server exited immediately with code {_process.ExitCode}");
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
