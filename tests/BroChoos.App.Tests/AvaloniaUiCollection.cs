using Xunit;

namespace BroChoos.App.Tests;

// The Avalonia Application, its Dispatcher and the platform services it starts are
// PROCESS-WIDE, and the Dispatcher is THREAD-AFFINE (a second bootstrap from another
// thread fails with "Call from invalid thread" rather than starting a second UI thread -
// MEASURED and recorded in PickerXamlLoadTests). xUnit runs test classes in parallel by
// default, so two UI-probing classes could otherwise race on that singleton state.
//
// This collection is the same tool the repo already uses for Console.SetOut/SetError
// (ConsoleCaptureCollection), applied to the other piece of process-wide mutable state in
// this assembly. Classes that construct real Avalonia windows join it, so their UI work is
// serialized rather than interleaved.
//
// LocalizationService.CurrentCulture is the third piece of process-wide state shared with
// ConsoleCaptureCollection, which runs in parallel with this one. Both definitions carry
// [CultureIsolated], which takes the global CultureGate around every test (see CultureGate
// for why the two collections cannot simply be merged).
[CollectionDefinition(Name)]
[CultureIsolated]
public sealed class AvaloniaUiCollection
{
	public const string Name = "avalonia-ui";
}
