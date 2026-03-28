using CopilotCliIde.Shared;
using Microsoft.VisualStudio.Shell;

namespace CopilotCliIde;

public partial class VsServiceRpc
{
	public async Task<SelectionResult> GetSelectionAsync()
	{
		await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
		try
		{
			var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
			var doc = dte?.ActiveDocument;
			if (doc?.Object("TextDocument") is not EnvDTE.TextDocument textDoc)
			{
				VsServices.Instance.Logger?.Log("Tool get_selection: (no editor)");
				return new SelectionResult { Current = false };
			}

			var sel = textDoc.Selection;
			var selectedText = sel.IsEmpty ? null : sel.Text;
			if (selectedText?.Length > 100_000)
			{
				selectedText = selectedText.Substring(0, 100_000);
			}

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
			VsServices.Instance.Logger?.Log($"Tool get_selection: {Path.GetFileName(result.FilePath ?? "")} L{(s.Start?.Line ?? 0) + 1}:{(s.Start?.Character ?? 0) + 1}{(s.IsEmpty ? "" : $" → L{(s.End?.Line ?? 0) + 1}:{(s.End?.Character ?? 0) + 1}")}");

			return result;
		}
		catch
		{
			VsServices.Instance.Logger?.Log("Tool get_selection: (no editor)");
			return new SelectionResult { Current = false };
		}
	}
}
