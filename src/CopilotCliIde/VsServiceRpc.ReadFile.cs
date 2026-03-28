using CopilotCliIde.Shared;

namespace CopilotCliIde;

public partial class VsServiceRpc
{
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

			VsServices.Instance.Logger?.Log($"Tool read_file: {Path.GetFileName(filePath)} ({totalLines} total, {end - start} returned)");

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
			VsServices.Instance.Logger?.Log($"Tool read_file: error: {ex.Message}");
			return Task.FromResult(new ReadFileResult { Error = ex.Message, FilePath = filePath });
		}
	}
}
