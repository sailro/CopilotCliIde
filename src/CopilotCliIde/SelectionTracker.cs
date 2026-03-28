using CopilotCliIde.Shared;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace CopilotCliIde;

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

	// When a frame is provided, the text view is obtained directly from it
	// (avoids IVsTextManager timing issues where GetActiveView hasn't updated yet).
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

	public void Reset()
	{
		_pendingKey = null;
		_pendingNotification = null;
		_pusher.Reset();
	}

	public void ResetDedupKey() => _pusher.ResetDedupKey();

	public void Dispose()
	{
		UntrackView();
		_pusher.Dispose();
	}

	private void OnSelectionChanged(object? sender, EventArgs e) => PushCurrentSelection();

	private void OnViewClosed(object? sender, EventArgs e) => UntrackView();

	// Captures selection data on UI thread and schedules a 200ms debounced push.
	private void PushCurrentSelection()
	{
		if (_getCallbacks() == null || _trackedView == null)
			return;

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

	// Sends the captured notification off the UI thread with dedup as a second filter.
	private void OnDebounceElapsed()
	{
		var notification = _pendingNotification;
		var key = _pendingKey;
		_pendingNotification = null;

		if (notification == null || key == null)
			return;

		if (_pusher.IsDuplicate(key))
			return;

		var callbacks = _getCallbacks();
		if (callbacks == null)
			return;

		var sel = notification.Selection;
		var isEmpty = sel?.IsEmpty ?? true;
		_logger?.Log($"Push selection_changed: {Path.GetFileName(notification.FilePath ?? "")} L{sel?.Start?.Line + 1}:{sel?.Start?.Character + 1}{(isEmpty ? "" : $" → L{sel?.End?.Line + 1}:{sel?.End?.Character + 1}")}");

		_ = Task.Run(async () =>
		{
			try { await callbacks.OnSelectionChangedAsync(notification); }
			catch { _clearCallbacks(null); }
		});
	}

	// Receives IVsMonitorSelection callbacks when the active window frame changes.
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
