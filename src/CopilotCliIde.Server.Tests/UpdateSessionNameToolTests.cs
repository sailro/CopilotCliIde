using CopilotCliIde.Server.Tools;

namespace CopilotCliIde.Server.Tests;

public class UpdateSessionNameToolTests
{
	[Fact]
	public void UpdateSessionName_ReturnsSuccess()
	{
		var result = UpdateSessionNameTool.UpdateSessionName("My Session");

		// Result should be an anonymous object with success = true
		var json = System.Text.Json.JsonSerializer.Serialize(result);
		Assert.Contains("\"success\":true", json);
	}

	[Fact]
	public void UpdateSessionName_EmptyName_StillSucceeds()
	{
		var result = UpdateSessionNameTool.UpdateSessionName("");

		var json = System.Text.Json.JsonSerializer.Serialize(result);
		Assert.Contains("\"success\":true", json);
	}

	[Fact]
	public void UpdateSessionName_LongName_StillSucceeds()
	{
		var longName = new string('A', 10_000);
		var result = UpdateSessionNameTool.UpdateSessionName(longName);

		var json = System.Text.Json.JsonSerializer.Serialize(result);
		Assert.Contains("\"success\":true", json);
	}

	[Fact]
	public void UpdateSessionName_SpecialCharacters_StillSucceeds()
	{
		var result = UpdateSessionNameTool.UpdateSessionName("café ☕ naïve 日本語");

		var json = System.Text.Json.JsonSerializer.Serialize(result);
		Assert.Contains("\"success\":true", json);
	}
}
