namespace CopilotCliIde;

internal static class BuildErrorLevelExtensions
{
	public static string ToProtocolSeverity(this EnvDTE80.vsBuildErrorLevel level) => level switch
	{
		EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelHigh => "error",
		EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelMedium => "warning",
		_ => "information"
	};
}
