using System.IO.Pipes;
using CopilotCliIde.Shared;
using StreamJsonRpc;

namespace CopilotCliIde.Server;

public sealed class RpcClient : IDisposable
{
	private NamedPipeClientStream? _pipe;
	private JsonRpc? _rpc;
	public IVsServiceRpc? VsServices { get; private set; }

	// Test-only: injects a pre-configured IVsServiceRpc to bypass the real named-pipe connection.
	internal RpcClient(IVsServiceRpc vsServices)
	{
		VsServices = vsServices;
	}

	public RpcClient() { }

	public event Func<SelectionNotification, Task>? SelectionChanged;

	public event Func<DiagnosticsChangedNotification, Task>? DiagnosticsChanged;

	public async Task ConnectAsync(string pipeName, CancellationToken ct = default)
	{
		_pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await _pipe.ConnectAsync(5000, ct);
		var callbacks = new McpServerCallbacks(this);
		_rpc = JsonRpc.Attach(_pipe, callbacks);
		VsServices = _rpc.Attach<IVsServiceRpc>();
	}

	internal Task RaiseSelectionChanged(SelectionNotification notification)
		=> SelectionChanged?.Invoke(notification) ?? Task.CompletedTask;

	internal Task RaiseDiagnosticsChanged(DiagnosticsChangedNotification notification)
		=> DiagnosticsChanged?.Invoke(notification) ?? Task.CompletedTask;

	public void Dispose()
	{
		_rpc?.Dispose();
		_pipe?.Dispose();
	}

	private sealed class McpServerCallbacks(RpcClient owner) : IMcpServerCallbacks
	{
		public Task OnSelectionChangedAsync(SelectionNotification notification)
			=> owner.RaiseSelectionChanged(notification);

		public Task OnDiagnosticsChangedAsync(DiagnosticsChangedNotification notification)
			=> owner.RaiseDiagnosticsChanged(notification);
	}
}
