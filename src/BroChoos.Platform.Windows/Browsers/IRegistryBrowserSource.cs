namespace BroChoos.Platform.Windows.Browsers;

// One raw registry-declared browser registration: the client key name as written
// by the vendor plus the verbatim shell "open" command. Parsing into an exe path
// happens in the detector, so the registry seam stays a pure data source.
public sealed record RegistryBrowserRegistration(string ClientKeyName, string ShellOpenCommand, string DetectionSource);

// D-181 tier 2/3: HKLM/HKCU Software\Clients\StartMenuInternet registrations and
// the RegisteredApplications index. Read-only by contract — nothing here writes
// to the registry in 0A-2.
public interface IRegistryBrowserSource
{
	IReadOnlyList<RegistryBrowserRegistration> GetRegistrations();
}
