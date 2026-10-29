using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Core.Runners;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.Platform.Windows.Tests;

public sealed class FirstRunNoScanIntegrationTests
{
	[Fact]
	public async Task PreConsent_InitialRender_ProbesRemainZero_AndBrowsersJsonIsNotCreated()
	{
		await using InstrumentedNoScanHarness harness = new();

		int exit = await harness.RunPrimaryAsync();

		_ = exit.Should().Be(ExitCodes.Success);
		harness.AssertNoPreConsentScanEvidence();
		_ = harness.PickerShowCount.Should().Be(1);
		_ = harness.FirstRunConsentRequired.Should().BeTrue();
	}

	[Fact]
	public async Task PreConsent_HelperTimeoutThenRestart_StillNoScanAndNoBrowsersJson()
	{
		await using InstrumentedNoScanHarness harness = new();

		Func<Task> timedOutRun = async () => _ = await harness.RunPrimaryAsync(pickerTimeout: true);
		_ = await timedOutRun.Should().ThrowAsync<TimeoutException>();

		int restartExit = await harness.RunPrimaryAsync();
		_ = restartExit.Should().Be(ExitCodes.Success);

		harness.AssertNoPreConsentScanEvidence();
		_ = harness.PickerShowCount.Should().Be(2, "restart should re-open picker without forcing discovery");
	}

	private sealed class InstrumentedNoScanHarness : IAsyncDisposable
	{
		private readonly InstrumentedProbeCounters _counters = new();
		private readonly FakeBrowserDetector _detector;
		private readonly FakePicker _picker;
		private readonly UrlRunner _runner;
		private readonly string _root;

		public InstrumentedNoScanHarness()
		{
			_root = Path.Combine(Path.GetTempPath(), $"brochoos-w3-4-noscan-{Guid.NewGuid():N}");
			_ = Directory.CreateDirectory(_root);

			_detector = new FakeBrowserDetector(_counters);
			_picker = new FakePicker();
			_runner = new UrlRunner(
				new ListLogger<UrlRunner>(),
				new FakeSingleInstanceGuard(SingleInstanceRole.Primary),
				_detector,
				_picker);
		}

		public int PickerShowCount => _picker.ShowCount;

		public bool FirstRunConsentRequired => _picker.LastRequest?.FirstRunConsent.IsRequired ?? false;

		private string BrowsersJsonPath => Path.Combine(_root, "browsers.json");

		public async Task<int> RunPrimaryAsync(bool pickerTimeout = false)
		{
			_picker.ThrowTimeout = pickerTimeout;
			return await _runner.RunAsync(BuildRequest(), CancellationToken.None);
		}

		public void AssertNoPreConsentScanEvidence()
		{
			_ = _detector.DetectCount.Should().Be(0, "W3.4 pre-consent contract forbids any discovery reads on startup path");
			_ = _counters.KnownPathReads.Should().Be(0);
			_ = _counters.RegistryReads.Should().Be(0);
			_ = _counters.ChromiumLocalStateReads.Should().Be(0);
			_ = _counters.FirefoxProfilesIniReads.Should().Be(0);
			_ = _counters.ProfileDirectoryReads.Should().Be(0);
			_ = File.Exists(BrowsersJsonPath).Should().BeFalse("W3.4 pre-consent path must not create browsers.json");
		}

		public ValueTask DisposeAsync()
		{
			try
			{
				if (Directory.Exists(_root))
				{
					Directory.Delete(_root, recursive: true);
				}
			}
			catch
			{
				// Best-effort cleanup in tests.
			}

			return ValueTask.CompletedTask;
		}
	}

	private sealed class InstrumentedProbeCounters
	{
		public int KnownPathReads { get; private set; }
		public int RegistryReads { get; private set; }
		public int ChromiumLocalStateReads { get; private set; }
		public int FirefoxProfilesIniReads { get; private set; }
		public int ProfileDirectoryReads { get; private set; }

		public void MarkDiscoveryPass()
		{
			KnownPathReads++;
			RegistryReads++;
			ChromiumLocalStateReads++;
			FirefoxProfilesIniReads++;
			ProfileDirectoryReads++;
		}
	}

	private sealed class FakeBrowserDetector(InstrumentedProbeCounters counters) : IBrowserDetector
	{
		private readonly InstrumentedProbeCounters _counters = counters;
		public int DetectCount { get; private set; }

		public IReadOnlyList<BrowserCandidate> Detect()
		{
			DetectCount++;
			_counters.MarkDiscoveryPass();
			return [];
		}
	}

	private sealed class FakePicker : IPicker
	{
		public int ShowCount { get; private set; }
		public PickerRequest? LastRequest { get; private set; }
		public bool ThrowTimeout { get; set; }

		public Task<int> ShowAsync(PickerRequest request, CancellationToken cancellationToken)
		{
			ShowCount++;
			LastRequest = request;

			return ThrowTimeout
				? throw new TimeoutException("simulated helper timeout during first-run flow")
				: Task.FromResult(ExitCodes.Success);
		}
	}

	private sealed class FakeSingleInstanceGuard(SingleInstanceRole role) : ISingleInstanceGuard
	{
		private readonly SingleInstanceRole _role = role;

		public Task<SingleInstanceRole> AcquireOrForwardAsync(ActivationRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(_role);
		}
	}

	private static ActivationRequest BuildRequest()
	{
		return new ActivationRequest(
			"https://example.com/",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Confirmed("test"),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));
	}

	private sealed class ListLogger<T> : ILogger<T>
	{
		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
		}
	}
}
