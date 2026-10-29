using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests;

public sealed class ExitCodesTests
{
	[Fact]
	public void ExitCodes_AreStable()
	{
		_ = ExitCodes.Success.Should().Be(0);
		_ = ExitCodes.GeneralError.Should().Be(1);
		_ = ExitCodes.NoArgs.Should().Be(2);
		_ = ExitCodes.InvalidUrl.Should().Be(3);
		_ = ExitCodes.NoBrowserDetected.Should().Be(4);
		_ = ExitCodes.BrowserLaunchFailed.Should().Be(5);
		_ = ExitCodes.SafeModeOk.Should().Be(10);
		_ = ExitCodes.SafeModeFailed.Should().Be(11);
		_ = ExitCodes.ConfigCorruption.Should().Be(99);
	}
}
