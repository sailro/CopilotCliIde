using System.Text;
using System.Text.Json;
using CopilotCliIde.Shared;

namespace CopilotCliIde.Server;

internal sealed class SseBroadcaster
{
	private readonly List<SseClient> _clients = [];
	private readonly Lock _lock = new();

	public void AddClient(SseClient client)
	{
		lock (_lock) { _clients.Add(client); }
	}

	public void RemoveClient(SseClient client)
	{
		lock (_lock) { _clients.Remove(client); }
	}

	public async Task BroadcastAsync(string method, object? @params)
	{
		var notification = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params });
		var sseEvent = $"event: message\ndata: {notification}\n\n";
		var chunkData = Encoding.UTF8.GetBytes(sseEvent);
		var chunk = Encoding.UTF8.GetBytes($"{chunkData.Length:x}\r\n");
		var chunkEnd = "\r\n"u8.ToArray();

		var fullChunk = new byte[chunk.Length + chunkData.Length + chunkEnd.Length];
		Buffer.BlockCopy(chunk, 0, fullChunk, 0, chunk.Length);
		Buffer.BlockCopy(chunkData, 0, fullChunk, chunk.Length, chunkData.Length);
		Buffer.BlockCopy(chunkEnd, 0, fullChunk, chunk.Length + chunkData.Length, chunkEnd.Length);

		SseClient[] clients;
		lock (_lock) { clients = [.. _clients]; }

		foreach (var client in clients)
		{
			try
			{
				await client.Pipe.WriteAsync(fullChunk);
				await client.Pipe.FlushAsync();
			}
			catch
			{
				client.Close();
			}
		}
	}

	public Task BroadcastSelectionChangedAsync(SelectionNotification notification)
	{
		return BroadcastAsync(Notification.SelectionChanged, new
		{
			text = notification.Text ?? "",
			filePath = notification.FilePath,
			fileUrl = notification.FileUrl,
			selection = notification.Selection == null ? null : new
			{
				start = new { line = notification.Selection.Start?.Line ?? 0, character = notification.Selection.Start?.Character ?? 0 },
				end = new { line = notification.Selection.End?.Line ?? 0, character = notification.Selection.End?.Character ?? 0 },
				isEmpty = notification.Selection.IsEmpty
			}
		});
	}

	public Task BroadcastDiagnosticsChangedAsync(DiagnosticsChangedNotification notification)
	{
		return BroadcastAsync(Notification.DiagnosticsChanged, new
		{
			uris = notification.Uris?.Select(u => new
			{
				uri = u.Uri,
				diagnostics = u.Diagnostics?.Select(d => new
				{
					range = d.Range == null ? null : new
					{
						start = new { line = d.Range.Start?.Line ?? 0, character = d.Range.Start?.Character ?? 0 },
						end = new { line = d.Range.End?.Line ?? 0, character = d.Range.End?.Character ?? 0 }
					},
					message = d.Message,
					severity = d.Severity,
					code = d.Code
				})
			})
		});
	}
}
