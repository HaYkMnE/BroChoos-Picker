using BroChoos.App.Picker;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// Finding A5: the handle-zero short-circuit. With hwnd == 0 the activation ladder
// would burn its whole budget on Win32 no-ops and end with FlashWindowEx(0); the
// caller checks this gate and skips the ladder (and the flash) entirely.
public sealed class ForegroundActivatorTests
{
	[Fact]
	public void IsAttemptable_ZeroHandle_IsRefused()
	{
		_ = ForegroundActivator.IsAttemptable(IntPtr.Zero).Should().BeFalse();
	}

	[Fact]
	public void IsAttemptable_RealHandle_IsAllowed()
	{
		_ = ForegroundActivator.IsAttemptable(new IntPtr(0x1234)).Should().BeTrue();
	}
}
