using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CopilotCliIde.Tools;

[McpServerToolType]
public sealed class GetSelectionTool
{
    [McpServerTool(Name = "get_selection"), Description("Get the current text selection from the active editor in Visual Studio")]
    public static object GetSelection()
    {
        // VS Extensibility out-of-proc does not currently provide direct editor text/selection APIs.
        // This is a placeholder that will be enhanced when VS Extensibility adds editor query support.
        return new
        {
            text = (string?)null,
            filePath = (string?)null,
            selection = (object?)null,
            current = false,
            message = "Editor selection API not yet available in VS Extensibility out-of-proc model"
        };
    }
}
