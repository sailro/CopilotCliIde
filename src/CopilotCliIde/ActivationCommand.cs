using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace CopilotCliIde;

/// <summary>
/// A hidden command that exists solely to trigger extension activation when a solution loads.
/// The VisibleWhen constraint causes VS to activate our extension process when a solution exists.
/// </summary>
[VisualStudioContribution]
internal class ActivationCommand : Command
{
    public ActivationCommand()
    {
    }

    public override CommandConfiguration CommandConfiguration => new("%CopilotCliIde.ActivationCommand.DisplayName%")
    {
        // This makes VS activate the extension when a solution is loaded
        VisibleWhen = ActivationConstraint.SolutionState(SolutionState.Exists),
        // Place in a non-visible location
        Placements = [],
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        // Never actually called — this command exists only to trigger extension activation
        return Task.CompletedTask;
    }
}
