using System.Globalization;
using BroChoos.App.Localization;
using BroChoos.App.Logging;
using BroChoos.App.Picker;
using BroChoos.Core.Storage;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace BroChoos.App.Settings;

// W8.1 — the Avalonia application for the settings lifetime. Mirrors PickerApp's wiring
// (FluentTheme + localization resources + OS accessibility) so the Settings shell opens
// with the same theme, language and reduced-motion treatment as the Picker, and adds the
// storage-backed SettingsViewModel as the single window's data context.
internal sealed class SettingsApp(ISettingsStore settingsStore, string? portableDataPath) : Application
{
	private readonly ISettingsStore _settingsStore = settingsStore;
	private readonly string? _portableDataPath = portableDataPath;

	public override void Initialize()
	{
		Styles.Add(new FluentTheme());
		LocalizationService.CurrentCulture = CultureInfo.CurrentUICulture;

		// Same OS-following theme as PickerApp: Light/Dark from the platform, and the shared
		// High Contrast variant when the OS asks for it, re-read on every colour change.
		ApplyPlatformTheme();
		if (PlatformSettings is { } platformSettings)
		{
			platformSettings.ColorValuesChanged += (_, _) => ApplyPlatformTheme();
		}
	}

	private void ApplyPlatformTheme()
	{
		RequestedThemeVariant = OsAccessibilitySettings.Read(PlatformSettings).ThemeVariant(PlatformSettings);
	}

	public override void OnFrameworkInitializationCompleted()
	{
		// Problem log: record UI-thread crashes (type only); never marks them handled.
		ProblemLog.AttachDispatcher(Dispatcher.UIThread);

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			// Closing the settings window ends the settings run; there is no other surface
			// in this lifetime, so the default OnLastWindowClose is the correct mode (unlike
			// the Picker, whose session owns when the run ends).
			SettingsViewModel viewModel = new(_settingsStore, _portableDataPath);
			desktop.MainWindow = new SettingsWindow(viewModel);
		}

		base.OnFrameworkInitializationCompleted();
	}
}
