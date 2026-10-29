using Avalonia.Styling;

namespace BroChoos.App.Picker;

// ARCHITECTURAL DECISION (owned by the picker's visual layer): how High Contrast is
// expressed as a theme variant.
//
// THE TWO OPTIONS, AND WHY NEITHER "STRING KEY IN XAML" NOR A BUILT-IN KEY WORKS.
//
//  1. A string key in the .axaml ThemeDictionaries - `x:Key="HighContrast"` - is what the
//     token file used to carry, and it cannot work at all. ThemeDictionaries is keyed by
//     ThemeVariant, and Avalonia's ThemeVariantTypeConverter is the only thing that can turn
//     that attribute into one. That converter accepts the BUILT-IN variants only and throws
//     NotSupportedException for anything else, with the message naming the remedy:
//     "ThemeVariant type converter supports only build in variants. For custom variants
//     please use x:Static markup extension." MEASURED: the throw happens during
//     compiled-XAML population of PickerTokens.axaml, i.e. while PickerWindow's
//     InitializeComponent runs, so the exception escaped PickerSession.OpenWindow() and
//     killed the process (exit 0xE0434352) on EVERY launch. The picker never opened once.
//
//  2. Reusing a BUILT-IN variant key (e.g. binding the HC palette to ThemeVariant.Light)
//     collides with the dictionaries already carrying those keys. Light, Dark and Default
//     are each already present in this file, so a second "Light" entry is a duplicate key
//     and the HC palette would be unreachable - and would silently overwrite the real Light
//     palette's role in the merge order.
//
// THE CHOSEN APPROACH, and the reason it is the only one that satisfies all three
// requirements at once: ONE ThemeVariant instance, declared in code, referenced from XAML by
// x:Static, and handed to Avalonia as the RequestedThemeVariant by the same code that reads
// the OS setting. Concretely:
//
//   (a) it COMPILES - x:Static is a compile-time reference, and it is the exact remedy the
//       framework's own exception message prescribes;
//   (b) it LOADS AT RUNTIME - the XAML key and the requested variant are the SAME instance,
//       so no key-matching subtlety is involved at all. (ThemeVariant also compares by key
//       string, so even a second instance built with this key would match; sharing the
//       instance simply removes the question.)
//   (c) it RESOLVES HC BRUSHES WHEN THE OS HC SETTING IS ON - OsAccessibilitySettings
//       returns this instance when the platform reports a high-contrast preference, and the
//       HC dictionary in PickerTokens.axaml then supplies every bc.* colour role.
//
// WHY THE HC DICTIONARY INHERITS LIGHT (ThemeVariant.Light as the parent): the HC dictionary
// overrides COLOUR ROLES only. Type, spacing, radius, motion and dwell tokens live in the
// root dictionary and must keep resolving, and the parent gives the variant a complete,
// always-resolvable baseline for any role the HC overlay does not name.
//
// KNOWN LIMITATION - THIS IS NOT THE USER'S HIGH-CONTRAST PALETTE. Avalonia 11.3 tells the app
// only THAT high contrast is on (PlatformColorValues.ContrastPreference) and whether the
// chosen scheme is light ("Desert", the only scheme whose name contains "White") or dark,
// plus the accent colour - VERIFIED against Avalonia 11.3 Win32PlatformSettings.GetColorValues
// and PlatformColorValues. It exposes none of the scheme's actual colours (text, background,
// hyperlink, button face, selected/highlight, disabled). FluentTheme 11.3 ships no
// high-contrast palette either: its BaseColorsPalette has only Default and Dark dictionaries.
// So the HC overlay maps onto Fluent's LIGHT brushes - strong black-on-white contrast, but not
// the user's chosen colours, and the parent is Light even when the user picked a dark scheme
// ("Aquatic", "Dusk", "Night sky"). Reading the real colours would need GetSysColor
// (COLOR_WINDOW, COLOR_WINDOWTEXT, COLOR_HIGHLIGHT, COLOR_HOTLIGHT, ...) mapped into the bc.*
// roles at runtime, which this build does not do (known limitation).
internal static class PickerThemeVariants
{
	// The single instance shared by the XAML dictionary key and the app's requested variant.
	// Public because x:Static resolves it by reflection from compiled XAML.
	public static readonly ThemeVariant HighContrast = new("HighContrast", ThemeVariant.Light);
}
