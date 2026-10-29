using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using BroChoos.App.Audit;
using BroChoos.App.Localization;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Routing;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

// 6c/6d: the card's accessible name and the localized failure/skip wording.
// LocalizationService.CurrentCulture is process-wide static state; serialized with the other culture-touching classes.
[Collection(ConsoleCaptureCollection.Name)]
public sealed partial class PickerLocalizationContractTests
{
	private static readonly string[] Cultures = ["en", "ru", "hy"];

	private static readonly string[] KnownCodes =
	[
		"launch.private-unsupported",
		"launch.start-failed",
		"launch.target-missing",
		"launch.helper-timeout",
		"launch.helper-not-found",
		"launch.recursion-guard",
		"consent.discovery.failed",
		"manual-browser.selector-unavailable",
		"manual-browser.path-rejected",
		"manual-browser.publish-failed",
	];

	[Fact]
	public void Packs_HaveTheSameKeys_AndTheSamePlaceholders()
	{
		IReadOnlyDictionary<string, string> en = LocalizationService.PackFor("en")!;
		_ = en.Should().NotBeNull();

		foreach (string culture in Cultures)
		{
			IReadOnlyDictionary<string, string>? pack = LocalizationService.PackFor(culture);
			_ = pack.Should().NotBeNull(culture);
			_ = pack!.Keys.Should().BeEquivalentTo(en.Keys, $"{culture} must define exactly the en keys");

			foreach ((string key, string value) in en)
			{
				_ = Placeholders(pack[key]).Should().BeEquivalentTo(Placeholders(value), $"{culture}:{key} must use the same placeholders as en");
			}
		}
	}

	// The full shortcut list is rendered as a two-column panel, one row per "keys<TAB>action"
	// line, so every pack must have the same number of rows and every row must split.
	[Fact]
	public void ShortcutHintPacks_HaveTheSameRows_AndEveryRowHasKeysAndAction()
	{
		int expected = LocalizationService.PackFor("en")!["picker.select.hints"].Split('\n').Length;

		foreach (string culture in Cultures)
		{
			string[] rows = LocalizationService.PackFor(culture)!["picker.select.hints"].Split('\n');
			_ = rows.Should().HaveCount(expected, $"{culture} must list the same shortcuts");
			_ = rows.Should().OnlyContain(row => row.Contains('\t'), $"{culture}: each row needs keys, a tab, then the action");
		}
	}

	[Fact]
	public void EveryLanguage_HasAPhrase_ForEveryKnownCode_AndTheNewStrings()
	{
		foreach (string culture in Cultures)
		{
			IReadOnlyDictionary<string, string> pack = LocalizationService.PackFor(culture)!;
			foreach (string code in KnownCodes)
			{
				_ = pack.Should().ContainKey("Error." + code, $"{culture} needs a phrase for {code}");
			}

			_ = pack.Should().ContainKeys(
				"Status.LaunchFailureWithReason",
				"Status.FirstRunSkipped",
				"Status.Target.AvailableBrowsers",
				"Status.Target.SystemFallback",
				"Status.Target.ManualAdd",
				"Card.AccessibleName");
		}
	}

	[Fact]
	public void LaunchFailure_KnownCode_ShowsTheLocalizedPhrase_AndKeepsTheRawCode()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("ru");
			PickerViewModel vm = NewViewModel();

			vm.ShowLaunchFailure("Microsoft Edge", "launch.private-unsupported");

			_ = vm.IsErrorStatusVisible.Should().BeTrue();
			_ = vm.StatusText.Should().Be(LocalizationService.GetString(
				"Status.LaunchFailureWithReason",
				"Microsoft Edge",
				LocalizationService.GetString("Error.launch.private-unsupported"),
				"launch.private-unsupported"));
			_ = vm.StatusText.Should().Contain("launch.private-unsupported", "the raw code stays for diagnostics");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public void LaunchFailure_UnknownCode_FallsBackToTheRawCode()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("hy");
			PickerViewModel vm = NewViewModel();

			vm.ShowLaunchFailure("Microsoft Edge", "storage.something-new");

			_ = vm.StatusText.Should().Be(LocalizationService.GetString("Status.LaunchFailure", "Microsoft Edge", "storage.something-new"));
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public async Task ManualAddFailure_NamesTheTargetInTheCurrentLanguage()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("ru");
			PickerViewModel vm = NewViewModel();
			FirstRunConsentActionCoordinator coordinator = new(new NoopStorageHelperClient());

			_ = await coordinator.HandleAsync(FirstRunAction.AddManually, vm, null, CancellationToken.None);

			_ = vm.StatusText.Should().Contain(LocalizationService.GetString("Status.Target.ManualAdd"));
			_ = vm.StatusText.Should().NotContain("manual add", "the target is a localized string, not an English literal");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public async Task Skip_ShowsItsOwnInfoStatus_NotAnError()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			PickerViewModel vm = NewViewModel();
			FirstRunConsentActionCoordinator coordinator = new(new NoopStorageHelperClient());

			_ = await coordinator.HandleAsync(FirstRunAction.Skip, vm, null, CancellationToken.None);

			_ = vm.IsErrorStatusVisible.Should().BeFalse("skipping the scan is a choice, not a failure");
			_ = vm.IsInfoStatusVisible.Should().BeTrue();
			_ = vm.StatusText.Should().Be(LocalizationService.GetString("Status.FirstRunSkipped"));
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	// Values that are legitimately the same in every language. Anything else identical to en in
	// ru or hy is an untranslated string. Each entry says why it may match.
	private static readonly Dictionary<string, string> IdenticalAllowlist = new(StringComparer.Ordinal)
	{
		["Card.AccessibleName"] = "format-only template: the browser name",
		["Card.AccessibleName.Profile"] = "format-only template: browser name, profile name",
		["Card.AccessibleName.More"] = "format-only template: browser name, hidden-profile phrase",
		["Card.AccessibleName.ProfileMore"] = "format-only template: browser name, profile name, hidden-profile phrase",
		["Card.ProfileMore"] = "format-only template: +count",
		["Settings.Support.Sha256Label"] = "algorithm name (SHA-256), not translated",
	};

	[Fact]
	public void RuAndHy_TranslateEveryValue_ExceptTheExplicitAllowlist()
	{
		IReadOnlyDictionary<string, string> en = LocalizationService.PackFor("en")!;
		List<string> untranslated = [];
		foreach (string culture in new[] { "ru", "hy" })
		{
			IReadOnlyDictionary<string, string> pack = LocalizationService.PackFor(culture)!;
			foreach ((string key, string value) in en)
			{
				if (!IdenticalAllowlist.ContainsKey(key) && string.Equals(pack[key], value, StringComparison.Ordinal))
				{
					untranslated.Add($"{culture}:{key}");
				}
			}
		}

		_ = untranslated.Should().BeEmpty("every ru/hy value must differ from en unless it is on the allowlist");
	}

	[Fact]
	public void NullCode_ShowsTheLocalizedUnknownWord()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("ru");
			PickerViewModel vm = NewViewModel();

			vm.ShowLaunchFailure("Microsoft Edge", null);

			_ = vm.StatusText.Should().Contain(LocalizationService.GetString("Status.UnknownCode"));
			_ = vm.StatusText.Should().NotContain("unknown", "the English word must not leak into ru");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public async Task DiscoveryFailure_UsesTheActionTemplate_NotLaunchWording()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			PickerViewModel vm = NewViewModel();
			FirstRunConsentActionCoordinator coordinator = new(new RejectingDiscoveryStorageHelperClient());

			_ = await coordinator.HandleAsync(FirstRunAction.FindBrowsers, vm, null, CancellationToken.None);

			_ = vm.StatusText.Should().Be(LocalizationService.GetString(
				"Status.ActionFailed",
				LocalizationService.GetString("Status.Target.AvailableBrowsers"),
				LocalizationService.GetString("Error.consent.discovery.failed"),
				"consent.discovery.failed"));
			_ = vm.StatusText.Should().NotContain("Could not open");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public void LaunchFailureWithReason_EveryLanguage_KeepsTheRecoveryHint()
	{
		foreach (string culture in Cultures)
		{
			string value = LocalizationService.PackFor(culture)!["Status.LaunchFailureWithReason"];

			_ = value.Should().MatchRegex(@"(?<![A-Za-z])F(?![A-Za-z])", $"{culture} must name the system-fallback key");
			_ = value.Should().MatchRegex(@"(?<![A-Za-z])Ctrl\+C(?![A-Za-z])", $"{culture} must name the copy key");
			_ = value.Should().MatchRegex(@"(?<![A-Za-z])Esc(?![A-Za-z])", $"{culture} must name the cancel key");
		}
	}

	// PickerSession.HandleFirstRunActionAsync: when the discovery seam THROWS (not rejects), the
	// session's catch must report an ACTION failure, never the "Could not open" launch wording.
	[Fact]
	public async Task DiscoveryThrows_SessionShowsTheActionFailure_NotLaunchWording()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("en");
			PickerSession session = NewSession(new ThrowingDiscoveryStorageHelperClient());
			string expected = LocalizationService.GetString(
				"Status.ActionFailed",
				LocalizationService.GetString("Status.Target.AvailableBrowsers"),
				LocalizationService.GetString("Error.consent.discovery.failed"),
				"consent.discovery.failed");

			session.FirstRunActionForTests(FirstRunAction.FindBrowsers);

			Stopwatch elapsed = Stopwatch.StartNew();
			while (!session.IsErrorStatusVisibleForTests && elapsed.Elapsed < TimeSpan.FromSeconds(10))
			{
				await Task.Delay(10);
			}

			_ = session.IsErrorStatusVisibleForTests.Should().BeTrue("the thrown discovery must surface as an error");
			_ = session.StatusMessageForTests.Should().Be(expected);
			_ = session.StatusMessageForTests.Should().NotContain("Could not open");
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	private static PickerSession NewSession(IStorageHelperClient storage)
	{
		BrowserSnapshot snapshot = new([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
		], 1, DateTimeOffset.UtcNow);
		ActivationRequest activation = new(
			"https://example.com/",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		return new PickerSession(
			new PickerRequest(activation, snapshot, FirstRunConsentContract.None),
			new UnusedLaunchHelperClient(),
			new FirstRunConsentActionCoordinator(storage),
			new UnusedFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance,
			trackingCleanupAuditWriter: new InertAuditWriter());
	}

	private sealed class InertAuditWriter : ITrackingCleanupAuditWriter
	{
		public void Write(TrackingCleanupAuditEvent cleanupEvent)
		{
			_ = cleanupEvent;
		}
	}

	private sealed class ThrowingDiscoveryStorageHelperClient : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("discovery seam broke its contract");
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "unused"));
		}
	}

	private sealed class UnusedLaunchHelperClient : ILaunchHelperClient
	{
		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new LaunchHelperOutcome(LaunchStatus.Failed, request.BrowserId, "unused", "launch", null));
		}
	}

	private sealed class UnusedFallbackLauncher : ISystemFallbackLauncher
	{
		public LaunchResult Launch(string url)
		{
			return new LaunchResult(LaunchStatus.Failed, "system", "unused", null, null);
		}
	}

	private sealed class RejectingDiscoveryStorageHelperClient : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "unused"));
		}
	}

	[Fact]
	public void Card_AccessibleName_CarriesTheBrowserName()
	{
		CultureInfo previous = LocalizationService.CurrentCulture;
		try
		{
			LocalizationService.CurrentCulture = CultureInfo.GetCultureInfo("ru");
			ChoiceCardViewModel card = new(
				new BrowserCandidate("chrome-stable", "Google Chrome", @"C:\Chrome\chrome.exe", "stable", "known-path"),
				2,
				2,
				RankingReasonCode.AllChoices,
				null);

			_ = card.AccessibleName.Should().Be(LocalizationService.GetString(
				"Card.AccessibleName",
				"Google Chrome"));
		}
		finally
		{
			LocalizationService.CurrentCulture = previous;
		}
	}

	[Fact]
	public void ChoiceCardButton_BindsItsAutomationName_ToTheAccessibleName()
	{
		string axaml = File.ReadAllText(Path.Combine(Root(), "src", "BroChoos.App", "Picker", "PickerWindow.axaml"));
		Match button = ChoiceCardButton().Match(axaml);

		_ = button.Success.Should().BeTrue("the choice-card button must exist");
		_ = button.Value.Should().Contain("AutomationProperties.Name=\"{Binding AccessibleName}\"");
	}

	[GeneratedRegex("<Button Classes=\"choiceCard\"[^>]*>")]
	private static partial Regex ChoiceCardButton();

	[GeneratedRegex(@"\{\d+\}")]
	private static partial Regex PlaceholderPattern();

	private static List<string> Placeholders(string value)
	{
		return [.. PlaceholderPattern().Matches(value).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];
	}

	private static PickerViewModel NewViewModel()
	{
		BrowserSnapshot snapshot = new([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
		], 1, DateTimeOffset.UtcNow);
		return new PickerViewModel("https://example.com/", snapshot);
	}

	private static string Root()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BroChoos.sln")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
	}

	private sealed class NoopStorageHelperClient : IStorageHelperClient
	{
		public Task<HelperClientDispatch> DispatchAsync(HelperCommand command, CancellationToken cancellationToken)
		{
			return Task.FromResult(HelperClientDispatch.Dispatched);
		}

		public Task<StorageDiscoveryResponse> DiscoverAndPersistAsync(StorageDiscoveryRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Dispatched, null, null));
		}

		public Task<StorageDiscoveryResponse> PublishCandidatesAsync(StorageCandidatePublicationRequest request, CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			return Task.FromResult(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.publish-not-used-by-this-test"));
		}
	}
}
