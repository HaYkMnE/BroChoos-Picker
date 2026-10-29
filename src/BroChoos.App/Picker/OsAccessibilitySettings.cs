using Avalonia.Platform;
using Avalonia.Styling;
using System.Runtime.InteropServices;

namespace BroChoos.App.Picker;

internal sealed record OsAccessibilitySettings(bool HighContrast, bool ReducedMotion)
{
	internal static OsAccessibilitySettings Read(IPlatformSettings? platformSettings)
	{
		PlatformColorValues? colors = platformSettings?.GetColorValues();
		bool highContrast = colors?.ContrastPreference.ToString().Contains("High", StringComparison.OrdinalIgnoreCase) == true;
		return new OsAccessibilitySettings(highContrast, ReadReducedMotion());
	}

	internal ThemeVariant ThemeVariant(IPlatformSettings? platformSettings)
	{
		if (HighContrast)
		{
			// The ONE code-defined variant instance the token dictionary is keyed by
			// (PickerThemeVariants.HighContrast). Handing Avalonia that same instance is
			// what makes the HC dictionary reachable: a custom variant cannot be written
			// as a string key in XAML at all - the framework's ThemeVariantTypeConverter
			// rejects it with NotSupportedException - so code and XAML must share this
			// instance rather than each building their own.
			return PickerThemeVariants.HighContrast;
		}

		string? systemVariant = platformSettings?.GetColorValues().ThemeVariant.ToString();
		return string.Equals(systemVariant, "Dark", StringComparison.OrdinalIgnoreCase)
			? Avalonia.Styling.ThemeVariant.Dark
			: Avalonia.Styling.ThemeVariant.Light;
	}

	private static bool ReadReducedMotion()
	{
		return OperatingSystem.IsWindows()
			&& SystemParametersInfo(0x1042, 0, out uint animationsEnabled, 0)
			&& animationsEnabled == 0;
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
}
