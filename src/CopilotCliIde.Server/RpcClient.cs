using System.IO.Pipes;
using CopilotCliIde.Shared;
using StreamJsonRpc;

namespace CopilotCliIde.Server;

public sealed class RpcClient : IDisposable
{
	private NamedPipeClientStream? _pipe;
	private JsonRpc? _rpc;
	public IVsServiceRpc? VsServices { get; private set; }

	/// <summary>
	/// Event raised when VS notifies the MCP server of a selection change.
	/// </summary>
	public event Func<SelectionNotification, Task>? SelectionChanged;

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

	public void Dispose()
	{
		_rpc?.Dispose();
		_pipe?.Dispose();
	}

	private sealed class McpServerCallbacks(RpcClient owner) : IMcpServerCallbacks
	{
		public Task OnSelectionChangedAsync(SelectionNotification notification)
			=> owner.RaiseSelectionChanged(notification);
	}
}
