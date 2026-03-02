using System.IO.Pipes;
using System.Runtime.InteropServices;
using CopilotCliIde.Server;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using StreamJsonRpc;
using Task = System.Threading.Tasks.Task;

namespace CopilotCliIde;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideBindingPath]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
[Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d")]
public sealed class CopilotCliIdePackage : AsyncPackage
{
    private IdeDiscovery? _discovery;
    private ServerProcessManager? _processManager;
    private NamedPipeServerStream? _rpcPipe;
    private JsonRpc? _rpc;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        try
        {
            _discovery = new IdeDiscovery();
            await _discovery.CleanStaleLockFilesAsync();

            var rpcPipeName = $"copilot-cli-rpc-{Guid.NewGuid()}";
            var mcpPipeName = $"mcp-{Guid.NewGuid()}.sock";
            var nonce = Guid.NewGuid().ToString();

            // Start RPC server for VS services
            _ = JoinableTaskFactory.RunAsync(() => StartRpcServerAsync(rpcPipeName, cancellationToken));

            // Start MCP server process
            _processManager = new ServerProcessManager();
            await _processManager.StartAsync(rpcPipeName, mcpPipeName, nonce);

            // Write lock file for Copilot CLI discovery
            var workspaceFolders = GetWorkspaceFolders();
            await _discovery.WriteLockFileAsync(mcpPipeName, nonce, workspaceFolders);
        }
        catch (Exception ex)
        {
            var diagPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".copilot", "ide", "vs-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(diagPath)!);
            File.WriteAllText(diagPath, $"{DateTime.UtcNow:O}\n{ex}");
        }
    }

    private async Task StartRpcServerAsync(string pipeName, CancellationToken ct)
    {
        _rpcPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await _rpcPipe.WaitForConnectionAsync(ct);
        _rpc = JsonRpc.Attach(_rpcPipe, new VsServiceRpc());
#pragma warning disable VSTHRD003 // Completion is a long-running task representing the RPC lifetime
        await _rpc.Completion;
#pragma warning restore VSTHRD003
    }

    private System.Collections.Generic.IReadOnlyList<string> GetWorkspaceFolders()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var dte = (EnvDTE80.DTE2)GetGlobalService(typeof(EnvDTE.DTE));
            if (dte?.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
            {
                var dir = Path.GetDirectoryName(dte.Solution.FullName);
                if (!string.IsNullOrEmpty(dir))
					return new List<string> { dir }.AsReadOnly();
            }
        }
        catch { }
        return new List<string> { Directory.GetCurrentDirectory() }.AsReadOnly();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _processManager?.Dispose();
            _rpc?.Dispose();
            _rpcPipe?.Dispose();
            _discovery?.Dispose();
        }
        base.Dispose(disposing);
    }
}
