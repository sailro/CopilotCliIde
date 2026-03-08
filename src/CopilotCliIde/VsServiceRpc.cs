using System.Collections.Concurrent;
using System.Diagnostics;
using CopilotCliIde.Shared;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CopilotCliIde;

public class VsServiceRpc : IVsServiceRpc
{
	private readonly ConcurrentDictionary<string, DiffState> _activeDiffs = new();

	internal static OutputLogger? Logger { get; set; }
	internal static Action? OnResetNotificationState { get; set; }

	private class DiffState
	{
		public string OriginalPath { get; set; } = "";
		public string TempNewPath { get; set; } = "";
		public string NewContent { get; set; } = "";
		public string TabName { get; set; } = "";
		public IVsWindowFrame? Frame { get; set; }
		public TaskCompletionSource<(string Result, string Trigger)>? Completion { get; set; }
		public IVsInfoBarUIElement? InfoBarElement { get; set; }
		public uint InfoBarCookie { get; set; }
	}

	public async Task<DiffResult> OpenDiffAsync(string originalFilePath, string newFileContents, string tabName)
	{
		Logger?.Log($"Tool open_diff: {tabName} ({originalFilePath})");
		try
		{
			originalFilePath = PathUtils.NormalizeFileUri(originalFilePath) ?? originalFilePath;

			// Close any existing diff with the same tab name
			var existingEntry = _activeDiffs.FirstOrDefault(kv => kv.Value.TabName == tabName);
			if (existingEntry.Key != null)
			{
				existingEntry.Value.Completion?.TrySetResult(("REJECTED", "closed_via_tool"));
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

			// Ultimate fallback: 1-hour timeout so we never block forever
			var timeoutCts = new CancellationTokenSource(TimeSpan.FromHours(1));
			timeoutCts.Token.Register(() => tcs.TrySetResult(("REJECTED", "timeout")), useSynchronizationContext: false);

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
				OriginalPath = originalFilePath,
				TempNewPath = tempFile,
				NewContent = newFileContents,
				TabName = tabName,
				Frame = frame,
				Completion = tcs
			};
			_activeDiffs[diffId] = state;

			// Add InfoBar with Accept/Reject buttons
			if (frame != null)
			{
				try { AddDiffInfoBar(frame, state); } catch { /* InfoBar is optional */ }
				try { MonitorFrameClose(frame, tcs); } catch { /* Frame monitoring is optional */ }
			}

			// Block until user accepts, rejects, or closes the diff
			var (result, trigger) = await tcs.Task.ConfigureAwait(false);

			// Clean up
			timeoutCts.Dispose();
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
			CleanupDiff(diffId);

			return new DiffResult
			{
				Success = true,
				DiffId = diffId,
				OriginalFilePath = originalFilePath,
				ProposedFilePath = tempFile,
				TabName = tabName,
				UserAction = result == "SAVED" ? "accepted" : "rejected",
				Result = result,
				Trigger = trigger,
				Message = result == "SAVED"
					? $"User accepted changes for {Path.GetFileName(originalFilePath)}"
					: $"User rejected changes for {Path.GetFileName(originalFilePath)}"
			};
		}
		catch (Exception ex)
		{
			return new DiffResult { Success = false, Error = ex.Message, OriginalFilePath = originalFilePath, TabName = tabName };
		}
	}

	public async Task<CloseDiffResult> CloseDiffByTabNameAsync(string tabName)
	{
		Logger?.Log($"Tool close_diff: {tabName}");
		var entry = _activeDiffs.FirstOrDefault(kv => kv.Value.TabName == tabName);
		if (entry.Key == null)
			return new CloseDiffResult { Success = true, AlreadyClosed = true, TabName = tabName, Message = $"No active diff found with tab name \"{tabName}\" (may already be closed)." };

		// Signal rejection to unblock OpenDiffAsync if it's waiting
		entry.Value.Completion?.TrySetResult(("REJECTED", "closed_via_tool"));

		if (!_activeDiffs.TryRemove(entry.Key, out var diff))
			return new CloseDiffResult { Success = true, AlreadyClosed = true, TabName = tabName, Message = $"No active diff found with tab name \"{tabName}\" (may already be closed)." };

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

			return new CloseDiffResult
			{
				Success = true,
				TabName = diff.TabName,
				OriginalFilePath = diff.OriginalPath,
				Message = $"Diff \"{tabName}\" closed and changes rejected"
			};
		}
		catch (Exception ex)
		{
			return new CloseDiffResult { Success = false, Error = ex.Message, TabName = tabName };
		}
	}

	public async Task<VsInfoResult> GetVsInfoAsync()
	{
		Logger?.Log("Tool get_vscode_info");
		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
		var result = new VsInfoResult { IdeName = "Visual Studio", AppName = "Visual Studio", ProcessId = Process.GetCurrentProcess().Id };
		try
		{
			var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
			result.Version = dte?.Version;
			if (dte?.Solution != null)
			{
				result.SolutionPath = dte.Solution.FullName;
				result.SolutionName = Path.GetFileNameWithoutExtension(result.SolutionPath);
				result.SolutionDirectory = Path.GetDirectoryName(result.SolutionPath);
				result.Projects = [];
				foreach (EnvDTE.Project p in dte.Solution.Projects)
					try { result.Projects.Add(new ProjectInfo { Name = p.Name, FullName = p.FullName }); } catch { /* Ignore */ }
			}
		}
		catch { /* Ignore */ }
		return result;
	}

	public async Task<SelectionResult> GetSelectionAsync()
	{
		Logger?.Log("Tool get_selection");
		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
		try
		{
			var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
			var doc = dte?.ActiveDocument;
			if (doc == null)
				return new SelectionResult { Current = false };

			if (doc.Object("TextDocument") is not EnvDTE.TextDocument textDoc)
				return new SelectionResult { Current = false };

			var sel = textDoc.Selection;
			string? selectedText = null;
			if (!sel.IsEmpty)
			{
				selectedText = sel.Text;
				if (selectedText?.Length > 100_000)
					selectedText = selectedText.Substring(0, 100_000);
			}

			return new SelectionResult
			{
				Current = true,
				FilePath = PathUtils.ToLowerDriveLetter(doc.FullName),
				FileUrl = PathUtils.ToVsCodeFileUrl(doc.FullName),
				Text = selectedText,
				Selection = new SelectionRange
				{
					Start = new SelectionPosition { Line = sel.TopPoint.Line - 1, Character = sel.TopPoint.DisplayColumn - 1 },
					End = new SelectionPosition { Line = sel.BottomPoint.Line - 1, Character = sel.BottomPoint.DisplayColumn - 1 },
					IsEmpty = sel.IsEmpty
				}
			};
		}
		catch
		{
			return new SelectionResult { Current = false };
		}
	}

	public async Task<DiagnosticsResult> GetDiagnosticsAsync(string? uri)
	{
		Logger?.Log($"Tool get_diagnostics: {uri ?? "(all)"}");
		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
		try
		{
			var filePath = PathUtils.NormalizeFileUri(uri);
			var files = ErrorListReader.CollectGrouped(filePath, maxItems: 100);
			return new DiagnosticsResult { Files = files };
		}
		catch (Exception ex) { return new DiagnosticsResult { Error = ex.Message }; }
	}


	public Task ResetNotificationStateAsync()
	{
		Logger?.Log("ResetNotificationState: new CLI client connected");
		OnResetNotificationState?.Invoke();
		return Task.CompletedTask;
	}

	public Task<ReadFileResult> ReadFileAsync(string filePath, int? startLine, int? maxLines)
	{
		Logger?.Log($"Tool read_file: {filePath}");
		try
		{
			filePath = PathUtils.NormalizeFileUri(filePath) ?? filePath;
			var fullText = File.ReadAllText(filePath);
			var allLines = fullText.Split('\n');
			var totalLines = allLines.Length;
			var start = Math.Max(0, (startLine ?? 1) - 1);
			var count = maxLines ?? totalLines;
			var end = Math.Min(totalLines, start + count);
			var slice = new string[end - start];
			Array.Copy(allLines, start, slice, 0, end - start);
			return Task.FromResult(new ReadFileResult
			{
				FilePath = filePath,
				Content = string.Join("\n", slice),
				TotalLines = totalLines,
				StartLine = start + 1,
				LinesReturned = end - start
			});
		}
		catch (Exception ex) { return Task.FromResult(new ReadFileResult { Error = ex.Message, FilePath = filePath }); }
	}

	private static void AddDiffInfoBar(IVsWindowFrame frame, DiffState state)
	{
		ThreadHelper.ThrowIfNotOnUIThread();

		if (Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory) return;

		var model = new InfoBarModel(
			"Copilot CLI: review proposed changes",
			[new InfoBarButton("Accept"), new InfoBarButton("Reject")],
			KnownMonikers.StatusInformation,
			isCloseButtonVisible: true);

		var uiElement = factory.CreateInfoBar(model);
		if (uiElement == null) return;

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
			return;

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
				tcs.TrySetResult(("SAVED", "accepted_via_button"));
			else if (actionItem.Text.Contains("Reject"))
				tcs.TrySetResult(("REJECTED", "rejected_via_button"));
		}

		public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			tcs.TrySetResult(("REJECTED", "rejected_via_button"));
		}
	}

	private sealed class FrameCloseNotify(TaskCompletionSource<(string Result, string Trigger)> tcs) : IVsWindowFrameNotify3
	{
		public int OnClose(ref uint pgrfSaveOptions)
		{
			tcs.TrySetResult(("REJECTED", "closed_via_tab"));
			return VSConstants.S_OK;
		}

		public int OnShow(int fShow) => VSConstants.S_OK;
		public int OnMove(int x, int y, int w, int h) => VSConstants.S_OK;
		public int OnSize(int x, int y, int w, int h) => VSConstants.S_OK;
		public int OnDockableChange(int fDockable, int x, int y, int w, int h) => VSConstants.S_OK;
	}
}
