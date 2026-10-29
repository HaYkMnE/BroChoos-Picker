using BroChoos.App.Logging;

namespace BroChoos.App.Picker;

// One-line console markers with the fixed [picker] prefix so the manual smoke can
// prove behaviour from the outside (keys routed, placement applied, activation
// verified, launch outcome, clipboard write, cancel), the same affordance style
// as the 0A-3 scaffold probe. Markers never carry the URL or the host (privacy
// rule): ids, codes, counts and coordinates only. Persistent log lines stay
// URL-free as well; the "Opening in <browser>: <url>" echo on a successful launch
// is the user's own input on their own console, exactly like the 0A-2 console path.
internal static class PickerLog
{
	public static void Marker(string message)
	{
		Console.WriteLine($"[picker] {message}");

		// Option A problem log: mapped context-step markers also go to problems.log, with
		// their id/code fields only (see ProblemLog.RecordMarker). No-op until installed.
		_ = ProblemLog.RecordMarker(ProblemLog.Current, message);
	}
}
