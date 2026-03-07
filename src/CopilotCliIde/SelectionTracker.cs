using CopilotCliIde.Shared;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace CopilotCliIde;

/// <summary>
/// Tracks the active editor's selection/caret position and pushes
/// debounced, deduplicated notifications to Copilot CLI via a callback.
/// Uses native VS editor APIs (IWpfTextView, IVsMonitorSelection) — no DTE COM interop.
/// </summary>
internal sealed class SelectionTracker : IDisposable
{
	private readonly IVsEditorAdaptersFactoryService _editorAdaptersFactory;
	private readonly Func<IMcpServerCallbacks?> _getCallbacks;
	private readonly Action<IMcpServerCallbacks?> _clearCallbacks;
	private readonly OutputLogger? _logger;
	private readonly DebouncePusher _pusher;
	private IWpfTextView? _trackedView;
	private volatile SelectionNotification? _pendingNotification;
	private volatile string? _pendingKey;

	public SelectionTracker(
		IVsEditorAdaptersFactoryService editorAdaptersFactory,
		Func<IMcpServerCallbacks?> getCallbacks,
		Action<IMcpServerCallbacks?> clearCallbacks,
		OutputLogger? logger = null)
	{
		_editorAdaptersFactory = editorAdaptersFactory;
		_getCallbacks = getCallbacks;
		_clearCallbacks = clearCallbacks;
		_logger = logger;
		_pusher = new DebouncePusher(OnDebounceElapsed);
	}

	/// <summary>
	/// Checks the active text view and subscribes to its selection/caret events.
	/// Called when the active window frame changes and on initial load.
	/// When a frame is provided, the text view is obtained directly from it
	/// (avoids IVsTextManager timing issues where GetActiveView hasn't updated yet).
	/// </summary>
	public void TrackActiveView(IVsWindowFrame? frame = null)
	{
		ThreadHelper.ThrowIfNotOnUIThread();

		IVsTextView? vsTextView = null;

		if (frame != null)
		{
			vsTextView = VsShellUtilities.GetTextView(frame);
		}
		else
		{
			var textManager = (IVsTextManager)Package.GetGlobalService(typeof(SVsTextManager));
			textManager?.GetActiveView(0, null, out vsTextView);
		}

		var wpfView = vsTextView != null ? _editorAdaptersFactory.GetWpfTextView(vsTextView) : null;

		if (wpfView == null)
		{
			UntrackView();
			return;
		}

		if (wpfView == _trackedView)
			return;

		UntrackView();

		_trackedView = wpfView;
		_trackedView.Selection.SelectionChanged += OnSelectionChanged;
		_trackedView.Closed += OnViewClosed;

		PushCurrentSelection();
	}

	public void UntrackView()
	{
		if (_trackedView == null)
			return;

		_trackedView.Selection.SelectionChanged -= OnSelectionChanged;
		_trackedView.Closed -= OnViewClosed;
		_trackedView = null;
	}

	/// <summary>
	/// Clears pending state and resets the debounce timer.
	/// Called on connection stop so the next connection starts fresh.
	/// </summary>
	public void Reset()
	{
		_pendingKey = null;
		_pendingNotification = null;
		_pusher.Reset();
	}

	/// <summary>
	/// Clears only the dedup key so the next event is always sent, even if
	/// the content hasn't changed. Called when a new CLI client connects.
	/// </summary>
	public void ResetDedupKey() => _pusher.ResetDedupKey();

	public void Dispose()
	{
		UntrackView();
		_pusher.Dispose();
	}

	private void OnSelectionChanged(object? sender, EventArgs e) => PushCurrentSelection();

	private void OnViewClosed(object? sender, EventArgs e) => UntrackView();

	/// <summary>
	/// Reads the current selection from the tracked IWpfTextView, captures
	/// the data on the UI thread, and schedules a debounced push (200ms)
	/// to Copilot CLI. Matches VS Code's 200ms selection debounce.
	/// </summary>
	private void PushCurrentSelection()
	{
		if (_getCallbacks() == null || _trackedView == null) return;

		try
		{
			var view = _trackedView;
			var selection = view.Selection;
			var snapshot = view.TextSnapshot;

			string? filePath = null;
			if (view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument? textDoc))
				filePath = textDoc?.FilePath;

			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
				return;

			var isEmpty = selection.IsEmpty;
			var startLine = snapshot.GetLineFromPosition(selection.Start.Position);
			var endLine = snapshot.GetLineFromPosition(selection.End.Position);
			var startLineNumber = startLine.LineNumber;
			var startCol = selection.Start.Position - startLine.Start.Position;
			var endLineNumber = endLine.LineNumber;
			var endCol = selection.End.Position - endLine.Start.Position;

			var selectedText = isEmpty
				? ""
				: snapshot.GetText(selection.Start.Position, selection.End.Position - selection.Start.Position);
			if (selectedText.Length > 10_000) selectedText = selectedText.Substring(0, 10_000);

			var key = $"{filePath}:{startLineNumber}:{startCol}:{endLineNumber}:{endCol}:{isEmpty}";

			_pendingKey = key;
			_pendingNotification = new SelectionNotification
			{
				Text = selectedText,
				FilePath = PathUtils.ToLowerDriveLetter(filePath!),
				FileUrl = PathUtils.ToVsCodeFileUrl(filePath!),
				Selection = new SelectionRange
				{
					Start = new SelectionPosition { Line = startLineNumber, Character = startCol },
					End = new SelectionPosition { Line = endLineNumber, Character = endCol },
					IsEmpty = isEmpty
				}
			};

			_pusher.Schedule();
		}
		catch { /* Don't crash VS */ }
	}

	/// <summary>
	/// Fires 200ms after the last selection change. Sends the captured
	/// notification off the UI thread, with deduplication as a second filter.
	/// </summary>
	private void OnDebounceElapsed()
	{
		var notification = _pendingNotification;
		var key = _pendingKey;
		_pendingNotification = null;

		if (notification == null || key == null) return;
		if (_pusher.IsDuplicate(key)) return;

		var callbacks = _getCallbacks();
		if (callbacks == null) return;

		var sel = notification.Selection;
		var isEmpty = sel?.IsEmpty ?? true;
		_logger?.Log($"Push selection_changed: {Path.GetFileName(notification.FilePath ?? "")} L{sel?.Start?.Line + 1}:{sel?.Start?.Character + 1}{(isEmpty ? "" : $" → L{sel?.End?.Line + 1}:{sel?.End?.Character + 1}")}");

		_ = Task.Run(async () =>
		{
			try { await callbacks.OnSelectionChangedAsync(notification); }
			catch { _clearCallbacks(null); }
		});
	}

	/// <summary>
	/// Receives IVsMonitorSelection callbacks when the active window frame changes.
	/// Triggers TrackActiveView to subscribe to the new editor's events.
	/// </summary>
	internal sealed class SelectionEventSink(SelectionTracker tracker) : IVsSelectionEvents
	{
		public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
			IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew) => VSConstants.S_OK;

		public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (elementid == (uint)VSConstants.VSSELELEMID.SEID_WindowFrame)
				tracker.TrackActiveView(varValueNew as IVsWindowFrame);
			return VSConstants.S_OK;
		}

		public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;
	}
}
