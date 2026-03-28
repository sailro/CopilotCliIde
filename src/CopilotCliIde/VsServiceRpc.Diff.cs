using System.Collections.Concurrent;
using CopilotCliIde.Shared;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CopilotCliIde;

public partial class VsServiceRpc
{
	private readonly ConcurrentDictionary<string, DiffState> _activeDiffs = new();

	private class DiffState
	{
		public string TempNewPath { get; set; } = "";
		public string TabName { get; set; } = "";
		public IVsWindowFrame? Frame { get; set; }
		public TaskCompletionSource<(string Result, string Trigger)>? Completion { get; set; }
		public IVsInfoBarUIElement? InfoBarElement { get; set; }
		public uint InfoBarCookie { get; set; }
	}

	public async Task<DiffResult> OpenDiffAsync(string originalFilePath, string newFileContents, string tabName)
	{
		VsServices.Instance.Logger?.Log($"Tool open_diff: {tabName} ({Path.GetFileName(originalFilePath)})");
		try
		{
			originalFilePath = PathUtils.NormalizeFileUri(originalFilePath) ?? originalFilePath;

			var existingEntry = _activeDiffs.FirstOrDefault(kv => kv.Value.TabName == tabName);
			if (existingEntry.Key != null)
			{
				existingEntry.Value.Completion?.TrySetResult((DiffOutcome.Rejected, DiffTrigger.ClosedViaTool));
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
				CleanupDiff(existingEntry.Key);
			}

			var ext = Path.GetExtension(originalFilePath);
			var tempDir = Path.Combine(Path.GetTempPath(), "copilot-cli-diffs");
			Directory.CreateDirectory(tempDir);
			var tempFile = Path.Combine(tempDir, $"{tabName}-proposed{ext}");
			File.WriteAllText(tempFile, newFileContents);

			var diffId = $"{DateTime.UtcNow.Ticks}-{tabName}";
			var tcs = new TaskCompletionSource<(string Result, string Trigger)>(TaskCreationOptions.RunContinuationsAsynchronously);

			var timeoutCts = new CancellationTokenSource(TimeSpan.FromHours(1));
			timeoutCts.Token.Register(() => tcs.TrySetResult((DiffOutcome.Rejected, DiffTrigger.ClosedViaTool)), useSynchronizationContext: false);

			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

			IVsWindowFrame? frame = null;
			var diffSvc = Package.GetGlobalService(typeof(SVsDifferenceService));
			if (diffSvc is IVsDifferenceService diffService)
			{
				frame = diffService.OpenComparisonWindow2(
					originalFilePath, tempFile,
					$"{tabName} (Proposed Changes)",
					"",
					Path.GetFileName(originalFilePath),
					$"{Path.GetFileName(originalFilePath)} (Proposed)",
					"",
					"",
					0);
				frame?.Show();
			}

			var state = new DiffState
			{
				TempNewPath = tempFile,
				TabName = tabName,
				Frame = frame,
				Completion = tcs
			};
			_activeDiffs[diffId] = state;

			if (frame != null)
			{
				try { AddDiffInfoBar(frame, state); } catch { /* InfoBar is optional */ }
				try { MonitorFrameClose(frame, tcs); } catch { /* Frame monitoring is optional */ }
			}

			var (result, trigger) = await tcs.Task.ConfigureAwait(false);
			VsServices.Instance.Logger?.Log($"Tool open_diff: {result} ({trigger})");

			timeoutCts.Dispose();
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
			CleanupDiff(diffId);

			return new DiffResult
			{
				Success = true,
				TabName = tabName,
				Result = result,
				Trigger = trigger,
				Message = result == DiffOutcome.Saved
					? $"User accepted changes for {Path.GetFileName(originalFilePath)}"
					: $"User rejected changes for {Path.GetFileName(originalFilePath)}"
			};
		}
		catch (Exception ex)
		{
			VsServices.Instance.Logger?.Log($"Tool open_diff: error: {ex.Message}");
			return new DiffResult { Success = false, Error = ex.Message, TabName = tabName };
		}
	}

	public async Task<CloseDiffResult> CloseDiffByTabNameAsync(string tabName)
	{
		var entry = _activeDiffs.FirstOrDefault(kv => kv.Value.TabName == tabName);
		if (entry.Key == null)
		{
			VsServices.Instance.Logger?.Log($"Tool close_diff: {tabName} (already closed)");
			return new CloseDiffResult { Success = true, AlreadyClosed = true, TabName = tabName, Message = $"No active diff found with tab name \"{tabName}\" (may already be closed)." };
		}

		entry.Value.Completion?.TrySetResult((DiffOutcome.Rejected, DiffTrigger.ClosedViaTool));

		if (!_activeDiffs.TryRemove(entry.Key, out var diff))
		{
			VsServices.Instance.Logger?.Log($"Tool close_diff: {tabName} (already closed)");
			return new CloseDiffResult { Success = true, AlreadyClosed = true, TabName = tabName, Message = $"No active diff found with tab name \"{tabName}\" (may already be closed)." };
		}

		try
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

			if (diff.InfoBarElement != null)
			{
				try { diff.InfoBarElement.Unadvise(diff.InfoBarCookie); diff.InfoBarElement.Close(); } catch { /* Ignore */ }
			}

			if (diff.Frame != null)
			{
				try { diff.Frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); } catch { /* Ignore */ }
			}

			try { File.Delete(diff.TempNewPath); } catch { /* Ignore */ }

			VsServices.Instance.Logger?.Log($"Tool close_diff: {tabName} (closed)");
			return new CloseDiffResult
			{
				Success = true,
				TabName = diff.TabName,
				Message = $"Diff \"{tabName}\" closed successfully"
			};
		}
		catch (Exception ex)
		{
			VsServices.Instance.Logger?.Log($"Tool close_diff: {tabName} (error: {ex.Message})");
			return new CloseDiffResult { Success = false, Error = ex.Message, TabName = tabName };
		}
	}

	private static void AddDiffInfoBar(IVsWindowFrame frame, DiffState state)
	{
		ThreadHelper.ThrowIfNotOnUIThread();

		if (Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory)
		{
			return;
		}

		var model = new InfoBarModel(
			"Copilot CLI: review proposed changes",
			[new InfoBarButton("Accept"), new InfoBarButton("Reject")],
			KnownMonikers.StatusInformation,
			isCloseButtonVisible: true);

		var uiElement = factory.CreateInfoBar(model);
		if (uiElement == null)
		{
			return;
		}

		var handler = new DiffInfoBarEvents(state.Completion!);
		uiElement.Advise(handler, out var cookie);

		if (ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID7.VSFPROPID_InfoBarHost, out var hostObj))
			&& hostObj is IVsInfoBarHost host)
		{
			host.AddInfoBar(uiElement);
		}

		state.InfoBarElement = uiElement;
		state.InfoBarCookie = cookie;
	}

	private static void MonitorFrameClose(IVsWindowFrame frame, TaskCompletionSource<(string Result, string Trigger)> tcs)
	{
		ThreadHelper.ThrowIfNotOnUIThread();
		frame.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, new FrameCloseNotify(tcs));
	}

	private void CleanupDiff(string diffId)
	{
		if (!_activeDiffs.TryRemove(diffId, out var diff))
		{
			return;
		}

		try
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			if (diff.InfoBarElement != null)
			{
				try { diff.InfoBarElement.Unadvise(diff.InfoBarCookie); diff.InfoBarElement.Close(); } catch { /* Ignore */ }
			}

			if (diff.Frame != null)
			{
				try { diff.Frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); } catch { /* Ignore */ }
			}

			try { File.Delete(diff.TempNewPath); } catch { /* Ignore */ }
		}
		catch { /* Ignore */ }
	}

	private sealed class DiffInfoBarEvents(TaskCompletionSource<(string Result, string Trigger)> tcs) : IVsInfoBarUIEvents
	{
		public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (actionItem.Text.Contains("Accept"))
			{
				tcs.TrySetResult((DiffOutcome.Saved, DiffTrigger.AcceptedViaButton));
			}
			else if (actionItem.Text.Contains("Reject"))
			{
				tcs.TrySetResult((DiffOutcome.Rejected, DiffTrigger.RejectedViaButton));
			}
		}

		public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			tcs.TrySetResult((DiffOutcome.Rejected, DiffTrigger.RejectedViaButton));
		}
	}

	private sealed class FrameCloseNotify(TaskCompletionSource<(string Result, string Trigger)> tcs) : IVsWindowFrameNotify3
	{
		public int OnClose(ref uint pgrfSaveOptions)
		{
			tcs.TrySetResult((DiffOutcome.Rejected, DiffTrigger.ClosedViaTool));
			return VSConstants.S_OK;
		}

		public int OnShow(int fShow) => VSConstants.S_OK;
		public int OnMove(int x, int y, int w, int h) => VSConstants.S_OK;
		public int OnSize(int x, int y, int w, int h) => VSConstants.S_OK;
		public int OnDockableChange(int fDockable, int x, int y, int w, int h) => VSConstants.S_OK;
	}
}
