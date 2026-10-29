namespace BroChoos.Core.Abstractions;

// D-183: the URL is passed as a separate structured argument (never a
// concatenated command string), UseShellExecute stays false, and no
// cmd/powershell intermediary is involved. Operational failures surface as
// LaunchResult, not as exceptions.
public interface IBrowserLauncher
{
	LaunchResult Launch(BrowserCandidate target, string url);
}
