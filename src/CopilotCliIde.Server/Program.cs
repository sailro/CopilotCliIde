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
rpcClient.SelectionChanged += notification => mcpServer.PushSelectionChangedAsync(notification);

// Forward diagnostics changes from VS to all connected CLI clients
rpcClient.DiagnosticsChanged += notification => mcpServer.PushDiagnosticsChangedAsync(notification);

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
try
{
	await mcpServer.DisposeAsync();
}
finally
{
	rpcClient.Dispose();
}
return 0;
