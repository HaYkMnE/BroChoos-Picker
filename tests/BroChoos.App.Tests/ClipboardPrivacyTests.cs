using BroChoos.App.Picker;
using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

public sealed class ClipboardPrivacyTests
{
	[Fact]
	public async Task ClipboardReadAtOpen_ReadsOnce_AndStoresOnlyInMemory()
	{
		PickerSession session = NewSession();
		session.ConfigureClipboardHooksForTests(async () => "https://clipboard.example/", _ => { });

		await session.CaptureClipboardAtOpenForTestsAsync();
		await session.CaptureClipboardAtOpenForTestsAsync();

		_ = session.ClipboardReadOnceForTests.Should().BeTrue();
		_ = session.ClipboardReadAtOpenValueForTests.Should().Be("https://clipboard.example/");
	}

	[Fact]
	public async Task CancelToClipboard_WritesOnDismiss_WhenUrlIsSafe()
	{
		string? written = null;
		PickerSession session = NewSession();
		session.ConfigureClipboardHooksForTests(null, value => written = value);

		int exit = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);

		_ = exit.Should().Be(ExitCodes.Success);
		_ = written.Should().Be("https://example.com/");
		_ = session.LastClipboardWriteForTests.Should().Be("https://example.com/");
	}

	[Fact]
	public async Task CancelToClipboard_SkipsSensitiveOrLocalTargets()
	{
		string? written = null;
		PickerSession session = NewSession("https://example.com/?access_token=abc");
		session.ConfigureClipboardHooksForTests(null, value => written = value);

		int exit = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);

		_ = exit.Should().Be(ExitCodes.Success);
		_ = written.Should().BeNull();
		_ = session.LastClipboardWriteForTests.Should().BeNull();
	}

	[Fact]
	public async Task ClipboardReadAtOpen_DoesNotRepopulateAfterCompletion()
	{
		TaskCompletionSource<string?> readTcs = new();
		PickerSession session = NewSession();
		session.ConfigureClipboardHooksForTests(async () => await readTcs.Task, _ => { });

		Task readTask = session.CaptureClipboardAtOpenForTestsAsync();
		await Task.Yield();
		_ = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);
		readTcs.SetResult("https://late.example/");
		await readTask;

		_ = session.ClipboardReadAtOpenValueForTests.Should().BeNull();
	}

	[Theory]
	[InlineData("file:///c:/secret.txt", true)]
	[InlineData("\\\\server\\share\\file", true)]
	[InlineData("https://localhost/app", true)]
	[InlineData("https://example.com/?token=abc", true)]
	[InlineData("https://example.com/", false)]
	public void SkipRule_CoversSensitiveAndSafeUrls(string url, bool expected)
	{
		_ = PickerSession.ShouldSkipCancelToClipboard(url).Should().Be(expected);
	}

	// W5.3 (spec 3.4): the read-at-open and cancel-to-clipboard switches are ON by default and
	// DISABLEABLE. Before this change both were hardcoded `{ get; } = true` on PickerSession, so
	// there was no way to turn either off - the "can be disabled" half of the contract was absent.
	// These pin the settings-driven seam on PickerRequest.

	[Fact]
	public async Task ClipboardReadAtOpen_IsSkipped_WhenDisabledByOptions()
	{
		bool readCalled = false;
		PickerSession session = NewSession(options: new PickerClipboardOptions(ClipboardReadAtOpenEnabled: false));
		session.ConfigureClipboardHooksForTests(
			async () =>
			{
				readCalled = true;
				return "https://clipboard.example/";
			},
			_ => { });

		await session.CaptureClipboardAtOpenForTestsAsync();

		_ = readCalled.Should().BeFalse("disabling the read switch must suppress the read entirely, not just discard its result");
		_ = session.ClipboardReadAtOpenValueForTests.Should().BeNull();
		_ = session.ClipboardReadOnceForTests.Should().BeFalse("a disabled read must not consume the once-per-open budget");
	}

	[Fact]
	public async Task CancelToClipboard_IsSkipped_WhenDisabledByOptions()
	{
		string? written = null;
		PickerSession session = NewSession(options: new PickerClipboardOptions(CancelToClipboardEnabled: false));
		session.ConfigureClipboardHooksForTests(null, value => written = value);

		int exit = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);

		_ = exit.Should().Be(ExitCodes.Success);
		_ = written.Should().BeNull("disabling cancel-to-clipboard must suppress the implicit write on Esc");
		_ = session.LastClipboardWriteForTests.Should().BeNull();
	}

	[Fact]
	public async Task ClipboardOptions_DefaultToBothSwitchesOn()
	{
		// The default-on half of the contract, pinned independently of the PickerSession fields:
		// a request with no clipboard options must read at open and write on cancel.
		_ = new PickerRequest(Activation("https://example.com/"), Snapshot(), FirstRunConsentContract.None)
			.EffectiveClipboardOptions.Should().Be(new PickerClipboardOptions(true, true));

		string? written = null;
		PickerSession session = NewSession();
		session.ConfigureClipboardHooksForTests(null, value => written = value);

		int exit = await session.ShowForTestsAsync([new PickerKeyAction(PickerKey.Escape, 0)]);

		_ = exit.Should().Be(ExitCodes.Success);
		_ = written.Should().Be("https://example.com/", "the default request keeps cancel-to-clipboard on, preserving the prior behaviour");
	}

	[Fact]
	public void ClipboardComment_DescribesTheRealReadAndWriteBehaviour()
	{
		// TRUTHFULNESS DEFECT (W5.3). The comment above CopyUrlAsync claimed "the product never
		// reads the user's clipboard" while OnWindowOpened calls ReadClipboardAtOpenAsync - a
		// comment that flatly contradicted the code three hundred lines above it. A source-level
		// assertion is the only way to pin a comment; the load-bearing half is the read call, which
		// the read-at-open tests above already exercise behaviourally.
		string source = File.ReadAllText(Path.Combine(Root(), "src", "BroChoos.App", "Picker", "PickerSession.cs"));

		_ = source.Should().Contain("ReadClipboardAtOpenAsync",
			"the read-at-open call must remain, since the corrected comment describes it");
		_ = source.Should().NotContain("The product never reads the",
			"the false claim must be gone: the product DOES read the clipboard at open");
	}

	private static ActivationRequest Activation(string url)
	{
		return new ActivationRequest(
			url,
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));
	}

	private static BrowserSnapshot Snapshot()
	{
		return new BrowserSnapshot([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
		], 1, DateTimeOffset.UtcNow);
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

	private static PickerSession NewSession(string url = "https://example.com/", PickerClipboardOptions? options = null)
	{
		BrowserSnapshot snapshot = new([
			new BrowserCandidate("edge-stable", "Microsoft Edge", @"C:\Edge\msedge.exe", "stable", "known-path")
		], 1, DateTimeOffset.UtcNow);

		ActivationRequest activation = new(
			url,
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Unknown(),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		return new PickerSession(
			new PickerRequest(activation, snapshot, FirstRunConsentContract.None, options),
			new FakeLaunchHelperClient(),
			new FirstRunConsentActionCoordinator(new FakeStorageHelperClient()),
			new FakeSystemFallbackLauncher(),
			new ForwardedUrlRouter(NullLogger<ForwardedUrlRouter>.Instance),
			NullLogger<PickerSession>.Instance);
	}

	private sealed class FakeLaunchHelperClient : ILaunchHelperClient
	{
		public Task<LaunchHelperOutcome> LaunchAsync(LaunchHelperRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(new LaunchHelperOutcome(LaunchStatus.Success, request.BrowserId, null, null, null));
		}
	}

	private sealed class FakeStorageHelperClient : IStorageHelperClient
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

	private sealed class FakeSystemFallbackLauncher : ISystemFallbackLauncher
	{
		public LaunchResult Launch(string url)
		{
			return new LaunchResult(LaunchStatus.Success, "system", null, null, null);
		}
	}
}
