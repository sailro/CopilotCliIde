using Microsoft.Extensions.DependencyInjection;

namespace CopilotCliIde.Server.Tests;

public class SingletonServiceProviderTests
{
	[Fact]
	public void GetService_ReturnsRpcClient()
	{
		var rpcClient = new RpcClient();
		var provider = new SingletonServiceProvider(rpcClient);

		var resolved = provider.GetService(typeof(RpcClient));

		Assert.Same(rpcClient, resolved);
	}

	[Fact]
	public void GetService_ReturnsNull_ForUnknownType()
	{
		var rpcClient = new RpcClient();
		var provider = new SingletonServiceProvider(rpcClient);

		Assert.Null(provider.GetService(typeof(string)));
		Assert.Null(provider.GetService(typeof(int)));
		Assert.Null(provider.GetService(typeof(HttpClient)));
	}

	[Fact]
	public void GetService_ReturnsSelf_ForIServiceProviderIsService()
	{
		var rpcClient = new RpcClient();
		var provider = new SingletonServiceProvider(rpcClient);

		var isService = provider.GetService(typeof(IServiceProviderIsService));

		Assert.Same(provider, isService);
	}

	[Fact]
	public void IsService_TrueForRpcClient()
	{
		var rpcClient = new RpcClient();
		var provider = new SingletonServiceProvider(rpcClient);
		var isServiceCheck = (IServiceProviderIsService)provider;

		Assert.True(isServiceCheck.IsService(typeof(RpcClient)));
	}

	[Fact]
	public void IsService_FalseForOtherTypes()
	{
		var rpcClient = new RpcClient();
		var provider = new SingletonServiceProvider(rpcClient);
		var isServiceCheck = (IServiceProviderIsService)provider;

		Assert.False(isServiceCheck.IsService(typeof(string)));
		Assert.False(isServiceCheck.IsService(typeof(HttpClient)));
	}
}
