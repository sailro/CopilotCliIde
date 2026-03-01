using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace CopilotCliIde;

/// <summary>
/// Tracks the active editor selection in VS by listening to text view changes.
/// Caches the last known selection so MCP tools can read it without an IClientContext.
/// </summary>
[VisualStudioContribution]
public sealed class SelectionTracker : ExtensionPart, ITextViewChangedListener, ITextViewOpenClosedListener, ITextViewExtension
{
    /// <summary>
    /// Cached selection state, accessible from MCP tools.
    /// </summary>
    internal static volatile SelectionState? LastSelection;

    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo = [DocumentFilter.FromGlobPattern("**/*", relativePath: false)],
    };

    public Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken)
    {
        CaptureSelection(args.AfterTextView);
        return Task.CompletedTask;
    }

    public Task TextViewOpenedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
    {
        CaptureSelection(textView);
        return Task.CompletedTask;
    }

    public Task TextViewClosedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static void CaptureSelection(ITextViewSnapshot textView)
    {
        var selection = textView.Selection;
        var filePath = textView.FilePath;

        string? selectedText = null;
        if (!selection.IsEmpty)
        {
            try
            {
                var doc = textView.Document;
                var startOffset = selection.Start.Offset;
                var endOffset = selection.End.Offset;
                var length = endOffset - startOffset;
                if (length > 0 && length < 100_000)
                {
                    var range = new TextRange(doc, startOffset, length);
                    var chars = new char[range.Length];
                    range.CopyTo(chars);
                    selectedText = new string(chars);
                }
            }
            catch { }
        }

        // Compute line/column from offset
        int startLine = 0, startCol = 0, endLine = 0, endCol = 0;
        try
        {
            var doc = textView.Document;
            startLine = doc.GetLineNumberFromPosition(selection.Start.Offset);
            endLine = doc.GetLineNumberFromPosition(selection.End.Offset);

            // Approximate column by counting back from offset to line start
            var startLineText = doc.Lines[startLine].Text;
            var startLineChars = new char[startLineText.Length];
            startLineText.CopyTo(startLineChars);
            var startLineStr = new string(startLineChars);
            // Column = offset within the line
            var lineStartOffset = selection.Start.Offset;
            for (int i = 0; i < startLine; i++)
                lineStartOffset -= doc.Lines[i].TextIncludingLineBreak.Length;
            startCol = lineStartOffset;

            var endLineStartOffset = selection.End.Offset;
            for (int i = 0; i < endLine; i++)
                endLineStartOffset -= doc.Lines[i].TextIncludingLineBreak.Length;
            endCol = endLineStartOffset;
        }
        catch { }

        LastSelection = new SelectionState
        {
            FilePath = filePath,
            FileUri = textView.Uri?.ToString(),
            SelectedText = selectedText,
            IsEmpty = selection.IsEmpty,
            StartLine = startLine,
            StartColumn = startCol,
            EndLine = endLine,
            EndColumn = endCol,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }
}

internal sealed class SelectionState
{
    public string? FilePath { get; init; }
    public string? FileUri { get; init; }
    public string? SelectedText { get; init; }
    public bool IsEmpty { get; init; }
    public int StartLine { get; init; }
    public int StartColumn { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
