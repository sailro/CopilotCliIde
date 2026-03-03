using System.IO.Pipes;
using CopilotCliIde.Shared;
using StreamJsonRpc;

namespace CopilotCliIde.Server;

public sealed class RpcClient : IDisposable
{
	private NamedPipeClientStream? _pipe;
	private JsonRpc? _rpc;
	public IVsServiceRpc? VsServices { get; private set; }

	public async Task ConnectAsync(string pipeName, CancellationToken ct = default)
	{
		_pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await _pipe.ConnectAsync(5000, ct);
		_rpc = JsonRpc.Attach(_pipe);
		VsServices = _rpc.Attach<IVsServiceRpc>();
	}

	public void Dispose()
	{
		_rpc?.Dispose();
		_pipe?.Dispose();
	}
}
