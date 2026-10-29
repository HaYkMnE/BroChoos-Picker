using BroChoos.Core.Abstractions;

namespace BroChoos.App.Picker;

// A browser the user picked themselves, on the way to W6.2 manual/portable admission.
// The picker is a small modal and cannot host a drive-browsing file dialog; the honest
// split is that the SURFACE (a file picker) supplies a path and this type carries it into
// the admission flow, which reuses the platform validator and publishes an admitted
// candidate through the same snapshot path consent-based discovery already uses.
//
// The path is NOT validated here. App must not grow a second validation story; the one
// gate lives in BroChoos.Platform.Windows.Browsers.ManualBrowserAdmitter.
internal interface IManualBrowserSelector
{
	bool TrySelectPath(ManualBrowserKind kind, out string? exePath, out string? displayName);
}
