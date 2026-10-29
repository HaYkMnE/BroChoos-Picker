namespace BroChoos.Core.Abstractions;

// D-268/D-496: reports the three-step Default Apps state per target, strictly
// read-only. The Selected step reads the CURRENT UserChoice ProgId as a snapshot
// and compares it against the ProgId this milestone declares; nothing here ever
// opens a writable key.
public interface IDefaultAppsStatusService
{
	// One entry per target (http, https, .url), in a fixed deterministic order.
	IReadOnlyList<DefaultAppsEntryStatus> GetStatus();
}
