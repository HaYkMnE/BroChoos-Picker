using BroChoos.Core.Cli;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Cli;

public sealed class HelpTextTests
{
	// 0A-5: --portable-data is the one option a user can be REJECTED over without ever
	// having seen it (CliParser.cs:163 and :168 -> ArgumentException -> exit 3, pinned in
	// BootstrapTests), so its absence from --help left the accepted spelling unlearnable
	// from inside the program. The value token is asserted SEPARATE, and the "=" spelling
	// asserted absent, because "--portable-data=<path>" is deliberately NOT this option
	// (CliParser.cs:222-226): help that showed the "=" form would be documenting a
	// rejection as if it were the contract.
	[Fact]
	public void Render_MentionsPortableDataOptionWithASeparateValueToken()
	{
		string help = HelpText.Render();

		_ = help.Should().Contain("--portable-data <path>");
		_ = help.Should().NotContain("--portable-data=");
	}

	// Public launch: the first line is the user-facing tagline, the same wording as the
	// Default Apps description. The old internal tagline must not come back.
	[Fact]
	public void Render_StartsWithThePublicTagline()
	{
		string help = HelpText.Render();

		_ = help.Should().StartWith("BroChoos Picker — choose which browser and profile opens each link");
		_ = help.Should().NotContain("Command Center");
	}

	// Docs audit: the build banner claimed the app "Detects Edge or Chrome", which stopped being
	// true when discovery moved behind first-run consent. The banner must state the real behaviour.
	[Fact]
	public void Render_DescribesConsentGatedDiscovery_NotTheStalePhaseBanner()
	{
		string help = HelpText.Render();

		_ = help.Should().Contain("Development build. Shows the Picker; the Picker discovers browsers only after first-run consent.");
		_ = help.Should().NotContain("Detects Edge or Chrome");
		_ = help.Should().NotContain("Phase 0A-5");
	}

	// W8.1: --settings is a real, documented operating mode. It must be listed in --help
	// so it is discoverable, and its conflict rule (it cannot be combined with another
	// mode) is enforced by the parser, not promised by prose.
	[Fact]
	public void Render_MentionsSettingsOption()
	{
		string help = HelpText.Render();

		_ = help.Should().Contain("--settings");
	}

	// W8.2: --safe-mode-action is a SELECTOR inside safe mode, not a mode of its own.
	// Bootstrap enters safe mode only on parsed.SafeMode (Bootstrap.cs:28), and the
	// parser sets that only for --safe-mode (CliParser.IsSafeModeOption); a bare
	// "--safe-mode-action <id>" is rejected as a usage error, exit 3 (pinned by
	// CliParserTests.Parse_SafeModeActionWithoutSafeMode_IsRejected and
	// BootstrapTests.RunAsync_SafeModeActionWithoutSafeMode_ReturnsUsageErrorAndNamesTheMissingFlag),
	// performing nothing. Help must therefore advertise
	// the form that actually runs the action (exercised end-to-end by
	// BootstrapTests.RunAsync_SafeModeWithAction_ForwardsTheSelectorAndPortableRootToRecovery)
	// and must not list the bare form as a usage line.
	[Fact]
	public void Render_AdvertisesSafeModeActionOnlyTogetherWithSafeMode()
	{
		string help = HelpText.Render();

		_ = help.Should().Contain("BroChoos.exe --safe-mode --safe-mode-action <id>");
		_ = help.Should().NotContain("BroChoos.exe --safe-mode-action");
	}
}
