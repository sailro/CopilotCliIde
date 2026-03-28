using Microsoft.Extensions.DependencyInjection;

namespace CopilotCliIde.Server;

internal sealed class SingletonServiceProvider(RpcClient rpcClient) : IServiceProvider, IServiceProviderIsService
{
	public object? GetService(Type serviceType)
	{
		if (serviceType == typeof(RpcClient))
			return rpcClient;

		return serviceType == typeof(IServiceProviderIsService)
			? this
			: null;
	}

	public bool IsService(Type serviceType)
	{
		return serviceType == typeof(RpcClient);
	}
}
