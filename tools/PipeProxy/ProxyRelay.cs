using System.IO.Pipes;
using System.Text;

namespace PipeProxy;

/// <summary>
/// Dead-simple bidirectional byte relay between two named pipe streams.
/// No HTTP parsing, no reconstruction — just raw byte forwarding.
/// Tees all bytes to a log stream for offline analysis.
/// </summary>
sealed class ProxyRelay(string vsCodePipeName, TrafficLogger logger)
{
	/// <summary>
	/// Handles one CLI pipe connection: opens a matching connection to VS Code's pipe,
	/// then relays raw bytes bidirectionally until either side disconnects.
	/// </summary>
	public async Task HandleConnectionAsync(NamedPipeServerStream cliPipe, CancellationToken ct)
	{
		var vsCodePipe = new NamedPipeClientStream(".", vsCodePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await vsCodePipe.ConnectAsync(5000, ct);

		try
		{
			// Two tasks: CLI→VSCode and VSCode→CLI, running simultaneously
			var cliToVs = RelayBytesAsync(cliPipe, vsCodePipe, "cli_to_vscode", ct);
			var vsToCli = RelayBytesAsync(vsCodePipe, cliPipe, "vscode_to_cli", ct);

			// Wait for either direction to end (pipe disconnect or cancel)
			await Task.WhenAny(cliToVs, vsToCli);
		}
		catch (OperationCanceledException) { }
		catch (IOException) { /* Pipe disconnected */ }
		finally
		{
			await vsCodePipe.DisposeAsync();
		}
	}

	/// <summary>
	/// Reads bytes from source and writes them to destination, logging everything.
	/// Runs until the source disconnects or cancellation is requested.
	/// </summary>
	private async Task RelayBytesAsync(Stream source, Stream destination, string direction, CancellationToken ct)
	{
		var buffer = new byte[8192];

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
				if (bytesRead == 0) break;

				await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
				await destination.FlushAsync(ct);

				// Log the raw bytes for offline analysis
				var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
				logger.LogRawBytes(direction, data);
			}
		}
		catch (OperationCanceledException) { }
		catch (IOException) { /* Pipe disconnected — normal */ }
	}
}
