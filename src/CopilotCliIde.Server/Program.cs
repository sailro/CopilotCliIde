using CopilotCliIde.Server;

var rpcPipe = args.SkipWhile(a => a != "--rpc-pipe").Skip(1).FirstOrDefault();
var mcpPipe = args.SkipWhile(a => a != "--mcp-pipe").Skip(1).FirstOrDefault();
var nonce = args.SkipWhile(a => a != "--nonce").Skip(1).FirstOrDefault();

if (rpcPipe == null || mcpPipe == null || nonce == null)
{
	Console.Error.WriteLine("Usage: --rpc-pipe <name> --mcp-pipe <name> --nonce <nonce>");
	return 1;
}

var rpcClient = new RpcClient();
await rpcClient.ConnectAsync(rpcPipe);

var mcpServer = new McpPipeServer();
await mcpServer.StartAsync(rpcClient, mcpPipe, nonce, CancellationToken.None);

// Forward selection changes from VS to all connected CLI clients
rpcClient.SelectionChanged += async notification =>
{
	await mcpServer.PushNotificationAsync("selection_changed", new
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
};

// Forward diagnostics changes from VS to all connected CLI clients
rpcClient.DiagnosticsChanged += async notification =>
{
	await mcpServer.PushNotificationAsync("diagnostics_changed", new
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
};

// Keep running until stdin closes (parent process dies) or cancellation
var tcs = new TaskCompletionSource<int>();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(0); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => tcs.TrySetResult(0);

// Also monitor stdin - if parent closes it, exit
_ = Task.Run(async () =>
{
	try { while (Console.In.ReadLine() != null) { } }
	catch { }
	tcs.TrySetResult(0);
});

await tcs.Task;
await mcpServer.DisposeAsync();
rpcClient.Dispose();
return 0;
