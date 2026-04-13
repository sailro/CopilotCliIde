using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace CopilotCliIde;

internal static class TerminalSettings
{
	public const string DefaultFontFamily = "Cascadia Code";
	public const short DefaultFontSize = 12;

	private const string CollectionPath = "CopilotCliIde\\Terminal";
	private const string FontFamilyKey = "FontFamily";
	private const string FontSizeKey = "FontSize";

	public static string FontFamily
	{
		get
		{
			try
			{
				var store = GetStore();
				if (store != null && store.CollectionExists(CollectionPath) && store.PropertyExists(CollectionPath, FontFamilyKey))
					return store.GetString(CollectionPath, FontFamilyKey);
			}
			catch { /* Ignore */ }

			return DefaultFontFamily;
		}
	}

	public static short FontSize
	{
		get
		{
			try
			{
				var store = GetStore();
				if (store != null && store.CollectionExists(CollectionPath) && store.PropertyExists(CollectionPath, FontSizeKey))
				{
					var size = store.GetInt32(CollectionPath, FontSizeKey);
					return (short)Math.Max(6, Math.Min(72, size));
				}
			}
			catch { /* Ignore */ }

			return DefaultFontSize;
		}
	}

	private static WritableSettingsStore? GetStore()
	{
		var sp = ServiceProvider.GlobalProvider;
		if (sp == null) return null;
		var manager = new ShellSettingsManager(sp);
		return manager.GetWritableSettingsStore(SettingsScope.UserSettings);
	}
}
