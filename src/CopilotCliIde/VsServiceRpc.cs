using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using CopilotCliIde.Shared;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CopilotCliIde;

public class VsServiceRpc : IVsServiceRpc
{
	private readonly ConcurrentDictionary<string, DiffState> _activeDiffs = new();

	private static readonly string _machineId = ComputeMachineId();
	private readonly string _sessionId = Guid.NewGuid().ToString() + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	internal static OutputLogger? Logger { get; set; }
	internal static Action? OnResetNotificationState { get; set; }

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
		Logger?.Log($"Tool open_diff: {tabName} ({originalFilePath})");
		try
		{
			originalFilePath = PathUtils.NormalizeFileUri(originalFilePath) ?? originalFilePath;

			// Close any existing diff with the same tab name
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

			// Ultimate fallback: 1-hour timeout so we never block forever
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

			// Add InfoBar with Accept/Reject buttons
			if (frame != null)
			{
				try { AddDiffInfoBar(frame, state); } catch { /* InfoBar is optional */ }
				try { MonitorFrameClose(frame, tcs); } catch { /* Frame monitoring is optional */ }
			}

			// Block until user accepts, rejects, or closes the diff
			var (result, trigger) = await tcs.Task.ConfigureAwait(false);
			Logger?.Log($"Tool open_diff: {result} ({trigger})");

			// Clean up
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
			Logger?.Log($"Tool open_diff: error: {ex.Message}");
			return new DiffResult { Success = false, Error = ex.Message, TabName = tabName };
		}
	}

	public async Task<CloseDiffResult> CloseDiffByTabNameAsync(string tabName)
	{
		var entry = _activeDiffs.FirstOrDefault(kv => kv.Value.TabName == tabName);
		if (entry.Key == null)
		{
			Logger?.Log("Tool close_diff: already closed");
			return new CloseDiffResult { Success = true, AlreadyClosed = true, TabName = tabName, Message = $"No active diff found with tab name \"{tabName}\" (may already be closed)." };
		}

		// Signal rejection to unblock OpenDiffAsync if it's waiting
		entry.Value.Completion?.TrySetResult((DiffOutcome.Rejected, DiffTrigger.ClosedViaTool));

		if (!_activeDiffs.TryRemove(entry.Key, out var diff))
		{
			Logger?.Log("Tool close_diff: already closed");
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

			Logger?.Log("Tool close_diff: closed");
			return new CloseDiffResult
			{
				Success = true,
				TabName = diff.TabName,
				Message = $"Diff \"{tabName}\" closed successfully"
			};
		}
		catch (Exception ex)
		{
			Logger?.Log($"Tool close_diff: error: {ex.Message}");
			return new CloseDiffResult { Success = false, Error = ex.Message, TabName = tabName };
		}
	}

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

		Logger?.Log($"Tool get_vscode_info: v{result.Version}");

		return result;
	}

	private static string ComputeMachineId()
	{
		using var sha = SHA256.Create();
		var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName));
		return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
	}

	public async Task<SelectionResult> GetSelectionAsync()
	{
		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
		try
		{
			var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
			var doc = dte?.ActiveDocument;
			if (doc?.Object("TextDocument") is not EnvDTE.TextDocument textDoc)
			{
				Logger?.Log("Tool get_selection: (no editor)");
				return new SelectionResult { Current = false };
			}

			var sel = textDoc.Selection;
			var selectedText = sel.IsEmpty ? null : sel.Text;
			if (selectedText?.Length > 100_000)
				selectedText = selectedText.Substring(0, 100_000);

			var result = new SelectionResult
			{
				Current = true,
				FilePath = PathUtils.ToLowerDriveLetter(doc.FullName),
				FileUrl = PathUtils.ToVsCodeFileUrl(doc.FullName),
				Text = selectedText,
				Selection = new SelectionRange
				{
					Start = new SelectionPosition { Line = sel.TopPoint.Line - 1, Character = sel.TopPoint.LineCharOffset - 1 },
					End = new SelectionPosition { Line = sel.BottomPoint.Line - 1, Character = sel.BottomPoint.LineCharOffset - 1 },
					IsEmpty = sel.IsEmpty
				}
			};

			var s = result.Selection;
			Logger?.Log($"Tool get_selection: {Path.GetFileName(result.FilePath ?? "")} L{(s.Start?.Line ?? 0) + 1}:{(s.Start?.Character ?? 0) + 1}{(s.IsEmpty ? "" : $" → L{(s.End?.Line ?? 0) + 1}:{(s.End?.Character ?? 0) + 1}")}");

			return result;
		}
		catch
		{
			Logger?.Log("Tool get_selection: (no editor)");
			return new SelectionResult { Current = false };
		}
	}

	public async Task<DiagnosticsResult> GetDiagnosticsAsync(string? uri)
	{
		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
		try
		{
			var filePath = PathUtils.NormalizeFileUri(uri);
			var files = ErrorListReader.CollectGrouped(filePath, maxItems: 100);
			var totalDiagnostics = files.Sum(f => f.Diagnostics?.Count ?? 0);
			var scope = filePath != null ? Path.GetFileName(filePath) : "(all)";
			Logger?.Log($"Tool get_diagnostics: {scope} {files.Count} file(s), {totalDiagnostics} diagnostic(s)");
			return new DiagnosticsResult { Files = files };
		}
		catch (Exception ex)
		{
			Logger?.Log($"Tool get_diagnostics: error: {ex.Message}");
			return new DiagnosticsResult { Error = ex.Message };
		}
	}


	public Task ResetNotificationStateAsync()
	{
		Logger?.Log("ResetNotificationState: new CLI client connected");
		OnResetNotificationState?.Invoke();
		return Task.CompletedTask;
	}

	public Task<ReadFileResult> ReadFileAsync(string filePath, int? startLine, int? maxLines)
	{
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

			Logger?.Log($"Tool read_file: {Path.GetFileName(filePath)} ({totalLines} total, {end - start} returned)");

			return Task.FromResult(new ReadFileResult
			{
				FilePath = filePath,
				Content = string.Join("\n", slice),
				TotalLines = totalLines,
				StartLine = start + 1,
				LinesReturned = end - start
			});
		}
		catch (Exception ex)
		{
			Logger?.Log($"Tool read_file: error: {ex.Message}");
			return Task.FromResult(new ReadFileResult { Error = ex.Message, FilePath = filePath });
		}
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
				tcs.TrySetResult((DiffOutcome.Saved, DiffTrigger.AcceptedViaButton));
			else if (actionItem.Text.Contains("Reject"))
				tcs.TrySetResult((DiffOutcome.Rejected, DiffTrigger.RejectedViaButton));
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
