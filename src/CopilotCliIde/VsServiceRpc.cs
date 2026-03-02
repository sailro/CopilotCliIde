using System.Collections.Concurrent;
using System.Diagnostics;
using CopilotCliIde.Shared;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CopilotCliIde;

public class VsServiceRpc : IVsServiceRpc
{
    private readonly ConcurrentDictionary<string, DiffState> _activeDiffs = new();

    private class DiffState
    {
        public string OriginalPath { get; set; } = "";
        public string TempNewPath { get; set; } = "";
        public string NewContent { get; set; } = "";
        public string TabName { get; set; } = "";
        public IVsWindowFrame? Frame { get; set; }
    }

    public async Task<DiffResult> OpenDiffAsync(string originalFilePath, string newFileContents, string tabName)
    {
        try
        {
            var ext = Path.GetExtension(originalFilePath);
            var tempDir = Path.Combine(Path.GetTempPath(), "copilot-cli-diffs");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, $"{tabName}-proposed{ext}");
            File.WriteAllText(tempFile, newFileContents);

            var diffId = $"{DateTime.UtcNow.Ticks}-{tabName}";

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

            _activeDiffs[diffId] = new DiffState
            {
                OriginalPath = originalFilePath, TempNewPath = tempFile,
                NewContent = newFileContents, TabName = tabName, Frame = frame
            };

            return new DiffResult
            {
                Success = true, DiffId = diffId, OriginalFilePath = originalFilePath,
                ProposedFilePath = tempFile, TabName = tabName,
                Message = $"Diff view opened (service={diffSvc?.GetType().Name}, frame={frame != null}). Use 'close_diff' with diffId='{diffId}' and action='accept' to apply, or 'reject' to discard."
            };
        }
        catch (Exception ex)
        {
            return new DiffResult { Success = false, Error = ex.Message, OriginalFilePath = originalFilePath, TabName = tabName };
        }
    }

    public async Task<CloseDiffResult> CloseDiffAsync(string diffId, string action)
    {
        if (!_activeDiffs.TryRemove(diffId, out var diff))
            return new CloseDiffResult { Success = false, Message = $"No active diff found with ID: {diffId}." };

        try
        {
            if (action.Equals("accept", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(diff.OriginalPath, diff.NewContent);

            if (diff.Frame != null)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try { diff.Frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); } catch { }
            }

            try { File.Delete(diff.TempNewPath); } catch { }

            return new CloseDiffResult
            {
                Success = true, Action = action, DiffId = diffId,
                TabName = diff.TabName, OriginalFilePath = diff.OriginalPath,
                Message = action.Equals("accept", StringComparison.OrdinalIgnoreCase)
                    ? $"Changes applied to {diff.OriginalPath}" : $"Changes discarded for {diff.OriginalPath}"
            };
        }
        catch (Exception ex)
        {
            return new CloseDiffResult { Success = false, Error = ex.Message, DiffId = diffId };
        }
    }

    public async Task<VsInfoResult> GetVsInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var result = new VsInfoResult { IdeName = "Visual Studio", ProcessId = Process.GetCurrentProcess().Id };
        try
        {
            var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
            if (dte?.Solution != null)
            {
                result.SolutionPath = dte.Solution.FullName;
                result.SolutionName = Path.GetFileNameWithoutExtension(result.SolutionPath);
                result.SolutionDirectory = Path.GetDirectoryName(result.SolutionPath);
                result.Projects = [];
                foreach (EnvDTE.Project p in dte.Solution.Projects)
                    try { result.Projects.Add(new ProjectInfo { Name = p.Name, FullName = p.FullName }); } catch { }
            }
        }
        catch { }
        return result;
    }

    public async Task<SelectionResult> GetSelectionAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
            var doc = dte?.ActiveDocument;
            if (doc == null)
                return new SelectionResult { Current = false, Message = "No active document." };

            if (doc.Object("TextDocument") is not EnvDTE.TextDocument textDoc)
                return new SelectionResult { Current = false, Message = "Active document is not a text document." };

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
                FilePath = doc.FullName,
                FileUri = new Uri(doc.FullName).ToString(),
                SelectedText = selectedText,
                IsEmpty = sel.IsEmpty,
                StartLine = sel.TopPoint.Line - 1,
                StartColumn = sel.TopPoint.DisplayColumn - 1,
                EndLine = sel.BottomPoint.Line - 1,
                EndColumn = sel.BottomPoint.DisplayColumn - 1,
                Timestamp = DateTimeOffset.UtcNow.ToString("O")
            };
        }
        catch (Exception ex)
        {
            return new SelectionResult { Current = false, Message = ex.Message };
        }
    }

    public async Task<DiagnosticsResult> GetDiagnosticsAsync(string? filePath)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = (EnvDTE80.DTE2)Package.GetGlobalService(typeof(EnvDTE.DTE));
            var errorItems = dte?.ToolWindows.ErrorList.ErrorItems;
            var results = new List<DiagnosticInfo>();
            if (errorItems != null)
            {
                for (int i = 1; i <= Math.Min(errorItems.Count, 100); i++)
                {
                    var item = errorItems.Item(i);
                    if (filePath != null && !string.IsNullOrEmpty(item.FileName) &&
                        !item.FileName.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    results.Add(new DiagnosticInfo
                    {
                        Severity = item.ErrorLevel.ToString(), Message = item.Description,
                        File = item.FileName, Line = item.Line, Column = item.Column, Project = item.Project
                    });
                }
            }
            return new DiagnosticsResult { Diagnostics = results };
        }
        catch (Exception ex) { return new DiagnosticsResult { Error = ex.Message }; }
    }

    public Task<ReadFileResult> ReadFileAsync(string filePath, int? startLine, int? maxLines)
    {
        try
        {
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
                FilePath = filePath, Content = string.Join("\n", slice),
                TotalLines = totalLines, StartLine = start + 1, LinesReturned = end - start
            });
        }
        catch (Exception ex) { return Task.FromResult(new ReadFileResult { Error = ex.Message, FilePath = filePath }); }
    }
}
