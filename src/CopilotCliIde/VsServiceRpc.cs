using CopilotCliIde.Shared;

namespace CopilotCliIde;

public partial class VsServiceRpc : IVsServiceRpc
{
	public Task ResetNotificationStateAsync()
	{
		VsServices.Instance.Logger?.Log("ResetNotificationState: new CLI client connected");
		VsServices.Instance.OnResetNotificationState?.Invoke();
		return Task.CompletedTask;
	}
}
