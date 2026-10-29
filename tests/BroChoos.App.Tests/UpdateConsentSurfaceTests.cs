using System.Globalization;
using BroChoos.App.Localization;
using BroChoos.App.Settings;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

[Collection(ConsoleCaptureCollection.Name)]
public sealed class UpdateConsentSurfaceTests
{
	[Fact]
	public void UnsignedOrUnverifiableBuild_ShowsDisabledVerificationAndNoTrustClaims()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			SettingsViewModel vm = new();

			_ = vm.UpdatesHeadingText.Should().Be("Updates");
			// No update checker is composed (Program.cs registers NoOpUpdateHelperClient), so there is
			// nothing to consent to; the text must not imply an opt-in exists.
			_ = vm.UpdatesConsentText.Should().Be("Not available in this release");
			_ = vm.IndicatorUpdatesText.Should().Be("Update check: not available in this release (nothing is checked automatically)");
			_ = vm.IsBuildVerificationAvailable.Should().BeFalse();
			_ = vm.VerifyBuildButtonText.Should().Be("Verify this build");
			_ = vm.VerifyBuildUnavailableText.Should().Be("Unavailable until a signed install is detected.");
			_ = vm.BuildHashText.Should().Be("Unavailable for unsigned or unverifiable builds.");
			_ = vm.Sha256Text.Should().Be("Unavailable for unsigned or unverifiable builds.");
			_ = vm.PublisherStatusText.Should().Be("Unsigned or unverifiable build");
			_ = vm.SupportPublisherLabelText.Should().Be("Publisher/signature status");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public void RuntimeVersionAndSupportLabels_AreRuntimeDerivedAndLocalized()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			SettingsViewModel enVm = new();

			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("hy");
			SettingsViewModel hyVm = new();

			_ = enVm.RuntimeVersionText.Should().NotBeNullOrWhiteSpace();
			_ = enVm.RuntimeVersionText.Should().NotContain("placeholder", "runtime version must not be a placeholder literal");

			_ = enVm.SupportRuntimeVersionLabelText.Should().NotBe(hyVm.SupportRuntimeVersionLabelText);
			_ = enVm.SupportReleaseChannelLabelText.Should().NotBe(hyVm.SupportReleaseChannelLabelText);
			_ = enVm.SupportBuildHashLabelText.Should().NotBe(hyVm.SupportBuildHashLabelText);
			_ = enVm.SupportPublisherLabelText.Should().NotBe(hyVm.SupportPublisherLabelText);
			_ = enVm.SupportSha256LabelText.Should().Be("SHA-256", "token stays literal across locales by design");
			_ = hyVm.SupportSha256LabelText.Should().Be("SHA-256", "token stays literal across locales by design");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public void UpdateVerificationTruthfulness_FixtureMatrix_SignedAndUnsignedClaimsStayConsistentAcrossLocales()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			string[] cultures = ["en", "ru", "hy"];
			foreach (string culture in cultures)
			{
				LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo(culture);
				SettingsViewModel unsignedFixture = new();
				SettingsViewModel signedFixture = new(
					buildHashTextOverride: "sha256:00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",
					publisherStatusTextOverride: "Signed by BroChoos Test Publisher",
					sha256TextOverride: "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

				_ = unsignedFixture.IsBuildVerificationAvailable.Should().BeFalse($"{culture} unsigned fixture must not expose verification as available");
				_ = unsignedFixture.VerifyBuildUnavailableText.Should().NotBe(unsignedFixture.VerifyBuildButtonText, $"{culture} unavailable help text must not equal action caption");
				_ = unsignedFixture.BuildHashText.Should().NotBe(unsignedFixture.SupportBuildHashLabelText, $"{culture} value must be state text, not label text");
				_ = unsignedFixture.Sha256Text.Should().NotBe(unsignedFixture.SupportSha256LabelText, $"{culture} value must be state text, not label text");
				_ = unsignedFixture.PublisherStatusText.Should().NotBe(unsignedFixture.SupportPublisherLabelText, $"{culture} value must be state text, not label text");

				_ = signedFixture.IsBuildVerificationAvailable.Should().BeTrue($"{culture} signed fixture must expose verification as available");
				_ = signedFixture.BuildHashText.Should().Contain("sha256:");
				_ = signedFixture.Sha256Text.Should().MatchRegex("^[0-9a-f]{64}$");
				_ = signedFixture.PublisherStatusText.Should().Contain("Signed by");
				_ = signedFixture.BuildHashText.Should().NotContain("Unavailable", $"{culture} signed fixture must not claim unavailable build hash");
				_ = signedFixture.Sha256Text.Should().NotContain("Unavailable", $"{culture} signed fixture must not claim unavailable sha256");
				_ = signedFixture.PublisherStatusText.Should().NotContain("unverifiable", $"{culture} signed fixture must not claim unverifiable publisher");
			}
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public void UpdateVerificationTruthfulness_WhitespaceEvidenceIsTreatedAsUnverifiable()
	{
		SettingsViewModel whitespaceFixture = new(
			buildHashTextOverride: "   ",
			publisherStatusTextOverride: "\t",
			sha256TextOverride: " ");

		_ = whitespaceFixture.IsBuildVerificationAvailable.Should().BeFalse(
			"empty/whitespace evidence must not be presented as signed/verifiable state");
	}
}
