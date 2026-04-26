using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace CopilotCliIde;

internal static class TerminalSettings
{
	public const string DefaultFontFamily = "Cascadia Code";
	public const short DefaultFontSize = 12;

	public const string DefaultExternalCommand = "cmd.exe";
	public const string DefaultExternalArguments = "/k copilot";

	private const string CollectionPath = "CopilotCliIde\\Terminal";
	private const string ExternalCollectionPath = "CopilotCliIde\\ExternalTerminal";

	public static string FontFamily => GetString(CollectionPath, TerminalSettingsProvider.FontFamilyKey, DefaultFontFamily);
	public static short FontSize => (short)Math.Max(6, Math.Min(72, GetInt32(CollectionPath, TerminalSettingsProvider.FontSizeKey, DefaultFontSize)));
	public static string ExternalCommand => GetString(ExternalCollectionPath, TerminalSettingsProvider.ExternalCommandKey, DefaultExternalCommand, requireNonBlank: true);
	public static string ExternalArguments => GetString(ExternalCollectionPath, TerminalSettingsProvider.ExternalArgumentsKey, DefaultExternalArguments);

	private static string GetString(string collection, string key, string defaultValue, bool requireNonBlank = false)
	{
		try
		{
			var store = GetStore();
			if (store != null && store.CollectionExists(collection) && store.PropertyExists(collection, key))
			{
				var value = store.GetString(collection, key);
				if (!requireNonBlank || !string.IsNullOrWhiteSpace(value))
					return value;
			}
		}
		catch { /* Ignore */ }

		return defaultValue;
	}

	private static int GetInt32(string collection, string key, int defaultValue)
	{
		try
		{
			var store = GetStore();
			if (store != null && store.CollectionExists(collection) && store.PropertyExists(collection, key))
				return store.GetInt32(collection, key);
		}
		catch { /* Ignore */ }

		return defaultValue;
	}

	private static WritableSettingsStore? GetStore()
	{
		var sp = ServiceProvider.GlobalProvider;
		if (sp == null)
			return null;

		var manager = new ShellSettingsManager(sp);
		return manager.GetWritableSettingsStore(SettingsScope.UserSettings);
	}
}
