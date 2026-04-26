using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;

namespace CopilotCliIde;

[Guid(ServiceGuid)]
internal sealed class TerminalSettingsProvider : IExternalSettingsProvider
{
	public const string ServiceGuid = "b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e";

	private const string CollectionPath = "CopilotCliIde\\Terminal";
	private const string ExternalCollectionPath = "CopilotCliIde\\ExternalTerminal";
	public const string FontFamilyKey = "FontFamily";
	public const string FontSizeKey = "FontSize";
	public const string ExternalCommandKey = "Command";
	public const string ExternalArgumentsKey = "Arguments";

	private readonly WritableSettingsStore _store;

#pragma warning disable CS0067
	public event EventHandler<ExternalSettingsChangedEventArgs>? SettingValuesChanged;
	public event EventHandler<EnumSettingChoicesChangedEventArgs>? EnumSettingChoicesChanged;
	public event EventHandler<DynamicMessageTextChangedEventArgs>? DynamicMessageTextChanged;
	public event EventHandler? ErrorConditionResolved;
#pragma warning restore CS0067

	public TerminalSettingsProvider(WritableSettingsStore store)
	{
		_store = store;
		if (!_store.CollectionExists(CollectionPath))
			_store.CreateCollection(CollectionPath);
		if (!_store.CollectionExists(ExternalCollectionPath))
			_store.CreateCollection(ExternalCollectionPath);
	}

	// Monikers in registration.json mirror the storage keys with a lowercase first letter
	// (e.g. "FontFamily" -> "fontFamily"). Treat them as the same identifier in two casings.
	private static string ToMoniker(string key) => char.ToLowerInvariant(key[0]) + key.Substring(1);

	private static bool Matches(string moniker, string key)
		=> moniker.EndsWith(ToMoniker(key), StringComparison.OrdinalIgnoreCase);

	public Task<ExternalSettingOperationResult<T>> GetValueAsync<T>(string moniker, CancellationToken cancellationToken) where T : notnull
	{
		object? value = null;

		if (Matches(moniker, FontFamilyKey))
		{
			value = _store.PropertyExists(CollectionPath, FontFamilyKey)
				? _store.GetString(CollectionPath, FontFamilyKey)
				: TerminalSettings.DefaultFontFamily;
		}
		else if (Matches(moniker, FontSizeKey))
		{
			value = _store.PropertyExists(CollectionPath, FontSizeKey)
				? _store.GetInt32(CollectionPath, FontSizeKey)
				: TerminalSettings.DefaultFontSize;
		}
		else if (Matches(moniker, ExternalCommandKey))
		{
			value = _store.PropertyExists(ExternalCollectionPath, ExternalCommandKey)
				? _store.GetString(ExternalCollectionPath, ExternalCommandKey)
				: TerminalSettings.DefaultExternalCommand;
		}
		else if (Matches(moniker, ExternalArgumentsKey))
		{
			value = _store.PropertyExists(ExternalCollectionPath, ExternalArgumentsKey)
				? _store.GetString(ExternalCollectionPath, ExternalArgumentsKey)
				: TerminalSettings.DefaultExternalArguments;
		}

		return value is not null
			? ExternalSettingOperationResult.SuccessResultTask((T)value)
			: ExternalSettingOperationResult.FailureResultTask<T>("Unknown setting", ExternalSettingsErrorScope.SingleSettingOnly, false);
	}

	public Task<ExternalSettingOperationResult> SetValueAsync<T>(string moniker, T value, CancellationToken cancellationToken) where T : notnull
	{
		if (Matches(moniker, FontFamilyKey) && value is string s)
		{
			_store.SetString(CollectionPath, FontFamilyKey, s);
		}
		else if (Matches(moniker, FontSizeKey) && value is int i)
		{
			_store.SetInt32(CollectionPath, FontSizeKey, Math.Max(6, Math.Min(72, i)));
		}
		else if (Matches(moniker, ExternalCommandKey) && value is string cmd)
		{
			_store.SetString(ExternalCollectionPath, ExternalCommandKey, cmd);
		}
		else if (Matches(moniker, ExternalArgumentsKey) && value is string args)
		{
			_store.SetString(ExternalCollectionPath, ExternalArgumentsKey, args);
		}
		else
		{
			return Task.FromResult<ExternalSettingOperationResult>(
				new ExternalSettingOperationResult.Failure("Unknown setting", ExternalSettingsErrorScope.SingleSettingOnly, false));
		}

		return ExternalSettingOperationResult.SuccessResultTask();
	}

	public async Task<ExternalSettingOperationResult<IReadOnlyList<EnumChoice>>> GetEnumChoicesAsync(string enumSettingMoniker, CancellationToken cancellationToken)
	{
		if (!Matches(enumSettingMoniker, FontFamilyKey))
			return ExternalSettingOperationResult.SuccessResult<IReadOnlyList<EnumChoice>>([]);

		await Task.Yield();

		var choices = new List<EnumChoice>();

		using var fonts = new InstalledFontCollection();
		foreach (var family in fonts.Families)
		{
			if (IsMonospaceFont(family.Name))
				choices.Add(new EnumChoice(family.Name, family.Name));
		}

		// Ensure defaults are always present
		EnsureChoice(choices, "Cascadia Code");
		EnsureChoice(choices, "Cascadia Mono");
		EnsureChoice(choices, "Consolas");

		choices.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

		return ExternalSettingOperationResult.SuccessResult<IReadOnlyList<EnumChoice>>(choices);
	}

	public Task<string> GetMessageTextAsync(string messageId, CancellationToken cancellationToken)
		=> Task.FromResult(string.Empty);

	public Task OpenBackingStoreAsync(CancellationToken cancellationToken)
		=> Task.CompletedTask;

	private static bool IsMonospaceFont(string fontName)
	{
		try
		{
			using var font = new System.Drawing.Font(fontName, 20f);
			using var bmp = new System.Drawing.Bitmap(1, 1);
			using var g = System.Drawing.Graphics.FromImage(bmp);
			// GenericTypographic removes GDI+ internal padding for accurate measurement
			var fmt = System.Drawing.StringFormat.GenericTypographic;
			var wSize = g.MeasureString("W", font, 0, fmt);
			var iSize = g.MeasureString("i", font, 0, fmt);
			return Math.Abs(wSize.Width - iSize.Width) < 1.0f;
		}
		catch
		{
			return false;
		}
	}

	private static void EnsureChoice(List<EnumChoice> choices, string fontName)
	{
		if (!choices.Exists(c => string.Equals(c.Moniker, fontName, StringComparison.OrdinalIgnoreCase)))
			choices.Add(new EnumChoice(fontName, fontName));
	}
}
