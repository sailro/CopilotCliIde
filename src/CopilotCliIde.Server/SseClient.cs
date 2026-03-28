using System.IO.Pipes;

namespace CopilotCliIde.Server;

internal sealed class SseClient(NamedPipeServerStream pipe)
{
	private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
	public NamedPipeServerStream Pipe => pipe;
	public Task WaitAsync(CancellationToken ct)
	{
		ct.Register(() => _done.TrySetResult());
		return _done.Task;
	}
	public void Close() => _done.TrySetResult();
}
