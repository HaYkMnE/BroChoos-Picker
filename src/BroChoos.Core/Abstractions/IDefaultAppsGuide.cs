namespace BroChoos.Core.Abstractions;

// D-274: after capabilities are declared, the user is guided to Windows Settings
// to pick BroChoos as the default. The interface is platform-neutral; only the
// Windows implementation knows how the settings page is opened (and documents
// why the UseShellExecute exception is required exactly there).
public interface IDefaultAppsGuide
{
	// Returns true when the shell accepted the request. A failure is fail-open:
	// the caller still shows the text guide and the verification step works
	// regardless, so an unopenable settings page never turns into a crash.
	bool TryOpenDefaultAppsPage();
}
