using System.IO.Pipes;
using PipeProxy;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
	ShowHelp();
	return 0;
}

if (args[0] != "capture")
{
	Console.Error.WriteLine($"Unknown command: {args[0]}");
	ShowHelp();
	return 1;
}

// Parse capture options
string? outputPath = null;
var verbose = false;

for (var i = 1; i < args.Length; i++)
{
	switch (args[i])
	{
		case "--output" or "-o":
			if (i + 1 < args.Length) outputPath = args[++i];
			break;
		case "--verbose" or "-v":
			verbose = true;
			break;
		default:
			Console.Error.WriteLine($"Unknown option: {args[i]}");
			return 1;
	}
}

return await RunCaptureAsync(outputPath, verbose);

// --- Local functions ---

static void ShowHelp()
{
	Console.Error.WriteLine("PipeProxy — Capture MCP traffic between Copilot CLI and VS Code");
	Console.Error.WriteLine();
	Console.Error.WriteLine("Usage:");
	Console.Error.WriteLine("  PipeProxy capture [--output <file>] [--verbose]");
	Console.Error.WriteLine();
	Console.Error.WriteLine("Commands:");
	Console.Error.WriteLine("  capture    Intercept and log MCP traffic as NDJSON");
	Console.Error.WriteLine();
	Console.Error.WriteLine("Options:");
	Console.Error.WriteLine("  --output, -o <file>  Write NDJSON to file (default: stdout)");
	Console.Error.WriteLine("  --verbose, -v        Show parsed traffic on stderr in real-time");
}

static async Task<int> RunCaptureAsync(string? outputPath, bool verbose)
{
	using var lockManager = new LockFileManager();

	// Step 1: Find VS Code
	Console.Error.WriteLine("Scanning for VS Code lock files...");
	var vsCodeLock = lockManager.FindVsCodeLock();
	if (vsCodeLock == null)
	{
		Console.Error.WriteLine("ERROR: No running VS Code instance found in ~/.copilot/ide/");
		Console.Error.WriteLine("Make sure VS Code Insiders is running with a workspace open.");
		return 1;
	}

	Console.Error.WriteLine($"Found: {vsCodeLock.IdeName} (PID {vsCodeLock.Pid})");
	Console.Error.WriteLine($"  Pipe: {vsCodeLock.PipeName}");
	Console.Error.WriteLine($"  Folders: {string.Join(", ", vsCodeLock.WorkspaceFolders)}");

	// Step 2: Create proxy pipe and lock file
	var proxyPipeName = $"copilot-proxy-{Guid.NewGuid():N}";
	lockManager.WriteProxyLock(vsCodeLock, proxyPipeName);
	Console.Error.WriteLine($"Proxy pipe: {proxyPipeName}");
	Console.Error.WriteLine("Proxy lock file written. VS Code's original lock hidden.");

	// Step 3: Set up traffic logger
	TextWriter logWriter = outputPath != null
		? new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8)
		: Console.Out;
	using var logger = new TrafficLogger(logWriter, verbose);
	var relay = new ProxyRelay(vsCodeLock.PipeName, logger);

	// Step 4: Accept CLI connections on proxy pipe
	using var cts = new CancellationTokenSource();
	Console.CancelKeyPress += (_, e) =>
	{
		e.Cancel = true;
		Console.Error.WriteLine("\nShutting down...");
		cts.Cancel();
	};

	Console.Error.WriteLine("Waiting for Copilot CLI to connect... (Ctrl+C to stop)");

	try
	{
		while (!cts.Token.IsCancellationRequested)
		{
			var pipe = new NamedPipeServerStream(
				proxyPipeName,
				PipeDirection.InOut,
				NamedPipeServerStream.MaxAllowedServerInstances,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous);

			try
			{
				await pipe.WaitForConnectionAsync(cts.Token);
				Console.Error.WriteLine("Client connected.");

				_ = Task.Run(async () =>
				{
					try
					{
						await relay.HandleConnectionAsync(pipe, cts.Token);
					}
					catch (Exception ex) when (ex is not OperationCanceledException)
					{
						Console.Error.WriteLine($"Connection error: {ex.Message}");
					}
					finally
					{
						await pipe.DisposeAsync();
					}
				}, cts.Token);
			}
			catch (OperationCanceledException)
			{
				await pipe.DisposeAsync();
				break;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Accept error: {ex.Message}");
				await pipe.DisposeAsync();
			}
		}
	}
	catch (OperationCanceledException) { /* Expected on Ctrl+C */ }

	Console.Error.WriteLine("Proxy stopped. Lock files restored.");
	return 0;
}
