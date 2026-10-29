using BroChoos.Core.Abstractions;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Abstractions;

// F4: the launch helper appended "--profile-directory=<id>" for EVERY browser. That switch
// is Chromium's; once Firefox reached the catalog (F1) the launch became
// "firefox.exe --profile-directory=default-release <url>". The profile argument is now
// chosen by browser family, derived from the BrowserId the request already carries.
public sealed class LaunchHelperArgumentsTests
{
	private const string Url = "https://example.com/";
	private const string Exe = @"C:\Browser\browser.exe";

	[Theory]
	[InlineData("edge-stable")]
	[InlineData("edge-canary")]
	[InlineData("chrome-stable")]
	[InlineData("chrome-beta")]
	[InlineData("brave-nightly")]
	[InlineData("EDGE-STABLE")]
	public void Build_Chromium_KeepsTheProfileDirectorySwitchBeforeTheUrl(string browserId)
	{
		IReadOnlyList<string> args = LaunchHelperArguments.Build(new LaunchHelperRequest(browserId, Exe, Url, false, "Profile 1"));

		_ = args.Should().Equal("--profile-directory=Profile 1", Url);
	}

	// Gecko gets NO profile argument. "-P <name>" opens Firefox's Profile Manager instead of
	// the URL when the name is not an exact (case-sensitive) profiles.ini Name, and the
	// catalog's first Firefox id is not the install's default profile; see the rationale on
	// LaunchHelperArguments. Without a switch Firefox uses its own default for the install
	// and remotes the URL into the running instance.
	[Theory]
	[InlineData("firefox-stable")]
	[InlineData("firefox-developer")]
	[InlineData("firefox-nightly")]
	public void Build_Gecko_PassesNoProfileArgumentAtAll(string browserId)
	{
		IReadOnlyList<string> args = LaunchHelperArguments.Build(new LaunchHelperRequest(browserId, Exe, Url, false, "default-release"));

		_ = args.Should().Equal(Url);
	}

	// Manual/portable entries are arbitrary user-picked executables: even one whose basis
	// says "chromium" is not proven to accept Chromium switches, so none is passed.
	[Theory]
	[InlineData("custom-chromium-chrome-0123abcd")]
	[InlineData("portable-chromium-msedge-89abcdef")]
	[InlineData("custom-myownbrowser-0123abcd")]
	[InlineData("opera-stable")]
	[InlineData("edge")]
	[InlineData("chromestable")]
	public void Build_CustomPortableOrUnknownFamily_PassesNoProfileSwitch(string browserId)
	{
		IReadOnlyList<string> args = LaunchHelperArguments.Build(new LaunchHelperRequest(browserId, Exe, Url, false, "Default"));

		_ = args.Should().Equal(Url);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Build_ChromiumWithoutAProfile_PassesOnlyTheUrl(string? profileId)
	{
		IReadOnlyList<string> args = LaunchHelperArguments.Build(new LaunchHelperRequest("chrome-stable", Exe, Url, false, profileId));

		_ = args.Should().Equal(Url);
	}

	// Private mode per family. The previous version of this test pinned edge-stable ->
	// "--incognito", which was wrong: Edge's switch is "--inprivate".
	[Theory]
	[InlineData("chrome-stable", "--incognito")]
	[InlineData("chrome-canary", "--incognito")]
	[InlineData("brave-stable", "--incognito")]
	[InlineData("brave-nightly", "--incognito")]
	[InlineData("edge-stable", "--inprivate")]
	[InlineData("edge-dev", "--inprivate")]
	[InlineData("EDGE-CANARY", "--inprivate")]
	public void TryBuild_ChromiumPrivate_UsesTheVendorSwitchBetweenProfileAndUrl(string browserId, string privateSwitch)
	{
		bool built = LaunchHelperArguments.TryBuild(
			new LaunchHelperRequest(browserId, Exe, Url, true, "Default"),
			out IReadOnlyList<string> args,
			out string? errorCode);

		_ = built.Should().BeTrue();
		_ = errorCode.Should().BeNull();
		_ = args.Should().Equal("--profile-directory=Default", privateSwitch, Url);
	}

	// Firefox: "-private-window <url>" with the URL as the flag's SEPARATE parameter
	// (BrowserContentHandler.sys.mjs: cmdLine.handleFlagWithParam("private-window") resolves
	// that parameter and opens it). The URL is NOT appended again as a positional argument,
	// which would open it a second time in a normal window.
	[Theory]
	[InlineData("firefox-stable")]
	[InlineData("firefox-developer")]
	[InlineData("FIREFOX-NIGHTLY")]
	public void TryBuild_GeckoPrivate_PassesPrivateWindowWithTheUrlAsItsParameter(string browserId)
	{
		bool built = LaunchHelperArguments.TryBuild(
			new LaunchHelperRequest(browserId, Exe, Url, true, "default-release"),
			out IReadOnlyList<string> args,
			out string? errorCode);

		_ = built.Should().BeTrue();
		_ = errorCode.Should().BeNull();
		_ = args.Should().Equal("-private-window", Url);
	}

	// No known private switch: refusing beats silently opening a NORMAL window the user
	// believes is private. The refusal is a typed error code, never an argv.
	[Theory]
	[InlineData("custom-chromium-chrome-0123abcd")]
	[InlineData("portable-chromium-msedge-89abcdef")]
	[InlineData("custom-myownbrowser-0123abcd")]
	[InlineData("opera-stable")]
	public void TryBuild_UnknownFamilyPrivate_IsRefusedWithPrivateUnsupported(string browserId)
	{
		bool built = LaunchHelperArguments.TryBuild(
			new LaunchHelperRequest(browserId, Exe, Url, true, null),
			out IReadOnlyList<string> args,
			out string? errorCode);

		_ = built.Should().BeFalse();
		_ = errorCode.Should().Be("launch.private-unsupported");
		_ = errorCode.Should().Be(LaunchHelperArguments.ErrorPrivateUnsupported);
		_ = args.Should().BeEmpty();
	}

	// Non-private launches of an unknown family stay exactly as before: URL only.
	[Fact]
	public void TryBuild_UnknownFamilyNotPrivate_StillBuildsTheUrlOnly()
	{
		bool built = LaunchHelperArguments.TryBuild(
			new LaunchHelperRequest("custom-myownbrowser-0123abcd", Exe, Url, false, "Default"),
			out IReadOnlyList<string> args,
			out string? errorCode);

		_ = built.Should().BeTrue();
		_ = errorCode.Should().BeNull();
		_ = args.Should().Equal(Url);
	}

	// Build is the throwing convenience over TryBuild; it must never hand back an argv for a
	// refused request.
	[Fact]
	public void Build_UnknownFamilyPrivate_Throws()
	{
		Action act = () => LaunchHelperArguments.Build(new LaunchHelperRequest("custom-x-0123abcd", Exe, Url, true, null));

		_ = act.Should().Throw<InvalidOperationException>().WithMessage("*launch.private-unsupported*");
	}

	[Theory]
	[InlineData("edge-stable", BrowserLaunchFamily.Chromium)]
	[InlineData("Chrome-Dev", BrowserLaunchFamily.Chromium)]
	[InlineData("brave-beta", BrowserLaunchFamily.Chromium)]
	[InlineData("firefox-stable", BrowserLaunchFamily.Gecko)]
	[InlineData("FIREFOX-NIGHTLY", BrowserLaunchFamily.Gecko)]
	[InlineData("custom-chromium-chrome-0123abcd", BrowserLaunchFamily.Unknown)]
	[InlineData("portable-firefox-0123abcd", BrowserLaunchFamily.Unknown)]
	[InlineData("", BrowserLaunchFamily.Unknown)]
	public void ResolveFamily_UsesTheDetectorIdPrefix(string browserId, BrowserLaunchFamily expected)
	{
		_ = LaunchHelperArguments.ResolveFamily(browserId).Should().Be(expected);
	}
}
