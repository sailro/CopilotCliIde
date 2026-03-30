using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CopilotCliIde;

internal sealed class OutputLogger
{
	private static readonly Guid _paneGuid = new("b8c2e7a1-3d4f-4e6a-9b8c-1a2b3c4d5e6f");
	private readonly IVsOutputWindowPane _pane;

	private OutputLogger(IVsOutputWindowPane pane) => _pane = pane;

	public static OutputLogger? Create()
	{
		ThreadHelper.ThrowIfNotOnUIThread();

		var outputWindow = (IVsOutputWindow?)Package.GetGlobalService(typeof(SVsOutputWindow));
		if (outputWindow == null) return null;

		var guid = _paneGuid;
		outputWindow.CreatePane(ref guid, "Copilot CLI IDE", fInitVisible: 1, fClearWithSolution: 1);
		outputWindow.GetPane(ref guid, out var pane);

		return pane != null ? new OutputLogger(pane) : null;
	}

	public void Log(string message)
	{
		try
		{
			// OutputStringThreadSafe is explicitly designed to be called from any thread
#pragma warning disable VSTHRD010
			_pane.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
#pragma warning restore VSTHRD010
		}
		catch { /* Never crash VS */ }
	}
}
