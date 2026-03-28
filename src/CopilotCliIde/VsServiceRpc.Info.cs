using System.Diagnostics;
using System.Security.Cryptography;
using CopilotCliIde.Shared;
using Microsoft.VisualStudio.Shell;

namespace CopilotCliIde;

public partial class VsServiceRpc
{
	private static readonly string _machineId = ComputeMachineId();
	private readonly string _sessionId = Guid.NewGuid().ToString() + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	public async Task<VsInfoResult> GetVsInfoAsync()
	{
		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
		var result = new VsInfoResult
		{
			AppName = "Visual Studio",
			Language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
			MachineId = _machineId,
			SessionId = _sessionId,
			UriScheme = "visualstudio",
			Shell = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
		};

		try
		{
			var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
			result.Version = dte?.Version;
		}
		catch { /* Ignore */ }

		try
		{
			result.AppRoot = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName);
		}
		catch { /* Ignore */ }

		VsServices.Instance.Logger?.Log($"Tool get_vscode_info: v{result.Version}");

		return result;
	}

	private static string ComputeMachineId()
	{
		using var sha = SHA256.Create();
		var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName));
		return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
	}
}
