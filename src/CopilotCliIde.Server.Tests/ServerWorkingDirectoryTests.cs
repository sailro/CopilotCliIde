using System.Diagnostics;
using System.IO.Pipes;

namespace CopilotCliIde.Server.Tests;

/// <summary>
/// Regression tests for GitHub Issue #4: the server process must set WorkingDirectory
/// to its own directory so that a hostile appsettings.json in VS's CWD does not crash Kestrel.
/// </summary>
public class ServerWorkingDirectoryTests
{
	private static readonly string _serverDir = FindServerDirectory();
	private static readonly string _serverDll = Path.Combine(_serverDir, "CopilotCliIde.Server.dll");

	private static string FindServerDirectory()
	{
		// Mirror the test output path to locate the server output directory
		var testOutputDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
		var serverDir = testOutputDir.Replace(
			"CopilotCliIde.Server.Tests",
			"CopilotCliIde.Server");

		if (!File.Exists(Path.Combine(serverDir, "CopilotCliIde.Server.dll")))
			throw new FileNotFoundException(
				$"Server DLL not found. Expected at: {Path.Combine(serverDir, "CopilotCliIde.Server.dll")}");

		return serverDir;
	}

	/// <summary>
	/// Regression test for Issue #4: when WorkingDirectory is set to the server's
	/// own directory, a hostile appsettings.json elsewhere does not crash Kestrel.
	/// </summary>
	[Fact]
	public async Task Server_WithCorrectWorkingDirectory_StartsWithoutCrash()
	{
		var tempDir = CreateHostileTempDir();
		Process? process = null;

		try
		{
			var (rpcPipe, mcpPipe, nonce) = GenerateNames();

			await using var rpcServer = new NamedPipeServerStream(
				rpcPipe, PipeDirection.InOut, 1,
				PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

			// Launch with CWD = server's own directory (the fix)
			process = LaunchServer(_serverDir, rpcPipe, mcpPipe, nonce);
			var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

			using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
			cts.CancelAfter(TimeSpan.FromSeconds(10));
			await rpcServer.WaitForConnectionAsync(cts.Token);

			// Wait for Kestrel to start (or crash)
			await Task.Delay(3000, TestContext.Current.CancellationToken);

			if (process.HasExited)
			{
				var stderr = await stderrTask;
				Assert.Fail(
					$"Server crashed unexpectedly (exit code {process.ExitCode}).\nStderr:\n{stderr}");
			}
		}
		finally
		{
			KillProcess(process);
			DeleteDirectory(tempDir);
		}
	}

	/// <summary>
	/// Proves Issue #4: when WorkingDirectory points to a directory with a hostile
	/// appsettings.json containing Kestrel HTTPS config, the server crashes.
	/// </summary>
	[Fact]
	public async Task Server_WithHostileAppsettingsInWorkingDirectory_Crashes()
	{
		var tempDir = CreateHostileTempDir();
		Process? process = null;

		try
		{
			var (rpcPipe, mcpPipe, nonce) = GenerateNames();

			await using var rpcServer = new NamedPipeServerStream(
				rpcPipe, PipeDirection.InOut, 1,
				PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

			// Launch with CWD = hostile directory (the bug)
			process = LaunchServer(tempDir, rpcPipe, mcpPipe, nonce);
			var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

			// The crash may happen before or after the RPC connection
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
			cts.CancelAfter(TimeSpan.FromSeconds(8));
			try
			{
				await rpcServer.WaitForConnectionAsync(cts.Token);
			}
			catch (OperationCanceledException)
			{
				// Process crashed before connecting — expected
			}

			var exited = process.WaitForExit(15000);
			if (!exited)
			{
				Assert.Fail("Server should have crashed from hostile appsettings.json but kept running");
			}

			Assert.NotEqual(0, process.ExitCode);
		}
		finally
		{
			KillProcess(process);
			DeleteDirectory(tempDir);
		}
	}

	private static string CreateHostileTempDir()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"cliide-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		File.WriteAllText(
			Path.Combine(dir, "appsettings.json"),
			"""
			{
			  "Kestrel": {
			    "Endpoints": {
			      "Https": {
			        "Url": "https://localhost:5001"
			      }
			    }
			  }
			}
			""");
		return dir;
	}

	private static Process LaunchServer(
		string workingDirectory, string rpcPipe, string mcpPipe, string nonce)
	{
		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				Arguments = $"\"{_serverDll}\" --rpc-pipe {rpcPipe} --mcp-pipe {mcpPipe} --nonce {nonce}",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WorkingDirectory = workingDirectory
			},
			EnableRaisingEvents = true
		};
		process.Start();
		return process;
	}

	private static (string rpcPipe, string mcpPipe, string nonce) GenerateNames()
		=> ($"test-rpc-{Guid.NewGuid():N}",
			$"test-mcp-{Guid.NewGuid():N}",
			Guid.NewGuid().ToString("N"));

	private static void KillProcess(Process? process)
	{
		if (process == null) return;
		try
		{
			if (!process.HasExited)
			{
				process.StandardInput.Close();
				if (!process.WaitForExit(3000))
					process.Kill();
			}
		}
		catch { /* Ignore */ }
		process.Dispose();
	}

	private static void DeleteDirectory(string path)
	{
		try { Directory.Delete(path, recursive: true); }
		catch { /* Ignore */ }
	}
}
