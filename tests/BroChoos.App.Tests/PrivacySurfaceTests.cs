using System.Globalization;
using BroChoos.App.Localization;
using BroChoos.App.Settings;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

[Collection(ConsoleCaptureCollection.Name)]
public sealed class PrivacySurfaceTests
{
	[Fact]
	public void SettingsPrivacySurface_ShowsAuthorityPrivacyClaims_AndNoTelemetryStatement()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			SettingsViewModel vm = new();

			_ = vm.PrivacyHeadingText.Should().Be("Privacy & network");
			_ = vm.PrivacyTelemetryText.Should().Be("No telemetry. Nothing is sent automatically.");
			// Both features are hardwired off (AvaloniaPicker: Enabled: false) and no setting turns them
			// on, so "off by default" would imply a switch that does not exist.
			_ = vm.PrivacyTrackingCleanupText.Should().Be("Tracking cleanup: not available in this release");
			_ = vm.PrivacyShortLinkText.Should().Be("Short-link expansion: not available in this release");
			_ = vm.IndicatorShortLinkText.Should().Be("Short-link expansion: not available in this release (planned, will ask before each use)");
			_ = vm.IndicatorTrackingCleanupText.Should().Be("Tracking cleanup: not available in this release");
			_ = vm.DashboardPrivacyText.Should().Be("Network: not used in this release");
			_ = vm.PrivacyClipboardText.Should().Contain("local and transient");
			_ = vm.PrivacyHistoryAllowlistText.Should().Contain("timestamp");
			_ = vm.PrivacyHistoryAllowlistText.Should().Contain("main domain");
			_ = vm.PrivacyHistoryAllowlistText.Should().NotContain("full URL", "history allowlist must not claim full-link storage");
			_ = vm.PrivacyHistoryControlsText.Should().Contain("Ctrl+H");
			_ = vm.PrivacyHistoryControlsText.Should().Contain("Ctrl+Backspace twice");
			_ = vm.PrivacyHistoryControlsText.Should().NotContain("disable", "Settings has no control to turn history off");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public void SettingsPrivacySurface_LocalizationSwitchesWithoutKeyFallback()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			SettingsViewModel enVm = new();

			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("ru");
			SettingsViewModel ruVm = new();

			_ = enVm.PrivacyTelemetryText.Should().NotBe(ruVm.PrivacyTelemetryText);
			_ = enVm.PrivacyHeadingText.Should().NotBe(ruVm.PrivacyHeadingText);
			_ = ruVm.PrivacyTelemetryText.Should().Contain("Телеметрии нет");
			_ = ruVm.PrivacyShortLinkText.Should().Contain("недоступно в этой версии");
			_ = ruVm.PrivacyHeadingText.Should().NotBe("Settings.Privacy.Heading");
			_ = ruVm.PrivacyTelemetryText.Should().NotBe("Settings.Privacy.Telemetry");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}
}
