using BroChoos.Core.Ingress;
using BroChoos.Core.Snapshots;

namespace BroChoos.Core.Abstractions;

// The 0A-3 Picker surface. Core declares only the contract: the implementation
// (Avalonia) lives in BroChoos.App so Core stays UI-free. Every accepted URL run
// routes here. --no-ui never does: UrlRunner rejects it (exit 1, nothing opened)
// before the single-instance acquire. The picker receives immutable ingress
// metadata plus immutable browser snapshot and returns the process exit code for
// the run (0 for a successful launch and for a user cancel, which is not a failure).
public enum FirstRunConsentStage
{
	NotRequired,
	Required,
}

public enum FirstRunAction
{
	FindBrowsers,
	AddManually,
	Skip,
}

public sealed record FirstRunConsentContract(FirstRunConsentStage Stage)
{
	public static FirstRunConsentContract None { get; } = new(FirstRunConsentStage.NotRequired);

	public static FirstRunConsentContract Required { get; } = new(FirstRunConsentStage.Required);

	public bool IsRequired => Stage == FirstRunConsentStage.Required;
}

// W5.3 (spec 3.4): "For each URL open, clipboard URL reading is ON by default and can be
// disabled", and cancel-to-clipboard is likewise on by default with a user setting to disable it.
// The defaults here ARE that default-on behaviour; the flags exist so a settings layer (or a
// test) can turn either switch off without touching the picker. This milestone has no persisted
// settings store yet (SettingsDocument deliberately persists nothing), so the composition root
// passes the defaults; wiring these to a real settings UI is the remaining step documented in
// the plan map.
public sealed record PickerClipboardOptions(
	bool ClipboardReadAtOpenEnabled = true,
	bool CancelToClipboardEnabled = true)
{
	public static PickerClipboardOptions Default { get; } = new();
}

public sealed record PickerRequest(
	ActivationRequest Activation,
	BrowserSnapshot Snapshot,
	FirstRunConsentContract FirstRunConsent,
	PickerClipboardOptions? ClipboardOptions = null)
{
	public string Url => Activation.NormalizedUrl;

	public IReadOnlyList<BrowserCandidate> Candidates => Snapshot.Candidates;

	// Never null for consumers: the default-on contract lives in PickerClipboardOptions.Default.
	public PickerClipboardOptions EffectiveClipboardOptions => ClipboardOptions ?? PickerClipboardOptions.Default;
}

public interface IPicker
{
	Task<int> ShowAsync(PickerRequest request, CancellationToken cancellationToken);
}
