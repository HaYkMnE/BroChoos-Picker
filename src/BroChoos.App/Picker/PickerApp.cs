using System.Globalization;
using BroChoos.App.Localization;
using BroChoos.App.Logging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BroChoos.App.Picker;

internal sealed class PickerApp(PickerSession session) : Application
{
	private readonly PickerSession _session = session;

	public override void Initialize()
	{
		Styles.Add(new FluentTheme());
		ApplyLocalizationResources();
		ApplyPlatformAccessibility();
		if (PlatformSettings is { } platformSettings)
		{
			platformSettings.ColorValuesChanged += OnPlatformColorsChanged;
		}
	}

	private void ApplyLocalizationResources()
	{
		LocalizationService.CurrentCulture = CultureInfo.CurrentUICulture;
		Resources["picker.select.hints"] = LocalizationService.GetString("picker.select.hints");
		Resources["GhostToast.Opened"] = LocalizationService.GetString("GhostToast.Opened");
		Resources["CleanupToast.Title"] = LocalizationService.GetString("CleanupToast.Title");
		Resources["CleanupToast.RestoreButton"] = LocalizationService.GetString("CleanupToast.RestoreButton");
	}

	private void ApplyPlatformAccessibility()
	{
		OsAccessibilitySettings settings = OsAccessibilitySettings.Read(PlatformSettings);
		RequestedThemeVariant = settings.ThemeVariant(PlatformSettings);
		Resources["bc.os.reducedMotion"] = settings.ReducedMotion;
		Resources["bc.motion.transitions.enabled"] = !settings.ReducedMotion;
		Resources["bc.motion.fast"] = settings.ReducedMotion ? 0d : 90d;
		Resources["bc.motion.normal"] = settings.ReducedMotion ? 0d : 180d;
		Resources["bc.motion.slow"] = settings.ReducedMotion ? 0d : 280d;
	}

	private void OnPlatformColorsChanged(object? sender, PlatformColorValues values)
	{
		ApplyPlatformAccessibility();
	}

	public override void OnFrameworkInitializationCompleted()
	{
		// Problem log: record UI-thread crashes (type only); never marks them handled.
		ProblemLog.AttachDispatcher(Dispatcher.UIThread);

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			// The session decides when (and with which exit code) the run ends; a
			// window closing must not implicitly shut the app down behind its back.
			desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
			_session.AttachDesktop(desktop);
			_session.OpenWindow();
		}

		base.OnFrameworkInitializationCompleted();
	}
}
