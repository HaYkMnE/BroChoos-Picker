#pragma warning disable IDE0058
#pragma warning disable IDE0305

using System.Reflection;
using BroChoos.Core;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Cli;
using BroChoos.Core.Ingress;
using BroChoos.Core.Runners;
using BroChoos.Core.Snapshots;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.Performance.Tests;

[Collection("ConsoleCapture")]
public sealed class FirstFramePathTests
{
	[Fact]
	public void BrowserSnapshot_DefaultEmpty_IsStableAndNoCandidates()
	{
		_ = BrowserSnapshot.Empty.IsEmpty.Should().BeTrue();
		_ = BrowserSnapshot.Empty.Candidates.Should().BeEmpty();
		_ = BrowserSnapshot.Empty.Generation.Should().Be(0);
	}

	[Fact]
	public void PickerRequest_UsesImmutableSnapshotSurface()
	{
		ActivationRequest activation = new(
			"https://example.com/",
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Confirmed("test"),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));

		BrowserSnapshot snapshot = BrowserSnapshot.Empty;
		PickerRequest request = new(activation, snapshot, FirstRunConsentContract.None);

		_ = request.Snapshot.Should().BeSameAs(snapshot);
		_ = request.Candidates.Should().BeSameAs(snapshot.Candidates);
		_ = request.Url.Should().Be("https://example.com/");
	}

	[Fact]
	public async Task Bootstrap_RunAsync_UrlPath_EmitsIngressMarker_BeforeSecondaryForwardMarker()
	{
		Bootstrap bootstrap = CreateBootstrap(SingleInstanceRole.Secondary);
		ConsoleRun run = await RunWithConsoleCaptureAsync(() => bootstrap.RunAsync(["https://example.com/"], CancellationToken.None));

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = run.StandardError.Should().BeEmpty();
		_ = run.StandardOutput.Should().Contain("[startup] INGRESS_ACCEPTED");
		_ = run.StandardOutput.Should().Contain("[secondary] Forwarded URL to primary instance");

		int ingressAt = run.StandardOutput.IndexOf("[startup] INGRESS_ACCEPTED", StringComparison.Ordinal);
		int secondaryAt = run.StandardOutput.IndexOf("[secondary] Forwarded URL to primary instance", StringComparison.Ordinal);
		_ = ingressAt.Should().BeGreaterThanOrEqualTo(0);
		_ = secondaryAt.Should().BeGreaterThan(ingressAt);
	}

	[Fact]
	public async Task Bootstrap_RunAsync_HelpPath_DoesNotEmitIngressMarker()
	{
		Bootstrap bootstrap = CreateBootstrap(SingleInstanceRole.Primary);
		ConsoleRun run = await RunWithConsoleCaptureAsync(() => bootstrap.RunAsync(["--help"], CancellationToken.None));

		_ = run.ExitCode.Should().Be(ExitCodes.Success);
		_ = run.StandardOutput.Should().NotContain("INGRESS_ACCEPTED");
	}

	[Fact]
	public void PickerLog_RuntimeMarkerEmission_PreservesPrefixAndOrder()
	{
		Type pickerLogType = Assembly.Load("BroChoos").GetType("BroChoos.App.Picker.PickerLog", throwOnError: true)!;
		MethodInfo markerMethod = pickerLogType.GetMethod("Marker", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

		ConsoleRun run = RunWithConsoleCapture(() =>
		{
			markerMethod.Invoke(null, ["OPEN_WINDOW_START"]);
			markerMethod.Invoke(null, ["FIRST_FRAME_REQUESTED"]);
			markerMethod.Invoke(null, ["FIRST_FRAME_RENDERED"]);
			markerMethod.Invoke(null, ["INTERACTIVE_READY"]);
		});

		_ = run.StandardError.Should().BeEmpty();

		string[] lines = run.StandardOutput
			.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);

		_ = lines.Should().ContainInOrder(
			"[picker] OPEN_WINDOW_START",
			"[picker] FIRST_FRAME_REQUESTED",
			"[picker] FIRST_FRAME_RENDERED",
			"[picker] INTERACTIVE_READY");
	}

	[Fact]
	public void PickerSession_Source_Contains_FirstFrameEvidenceMarkers()
	{
		string source = ReadRepoFile("src", "BroChoos.App", "Picker", "PickerSession.cs");

		_ = source.Should().Contain("OPEN_WINDOW_START");
		_ = source.Should().Contain("FIRST_FRAME_REQUESTED");
		_ = source.Should().Contain("FIRST_FRAME_RENDERED");
		_ = source.Should().Contain("INTERACTIVE_READY");
		_ = source.Should().Contain("LAUNCH_REQUESTED");
	}

	private static Bootstrap CreateBootstrap(SingleInstanceRole role)
	{
		ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
		{
			_ = builder.SetMinimumLevel(LogLevel.None);
		});

		CliParser parser = new(new PassThroughUrlValidator());
		UrlRunner urlRunner = new(
			loggerFactory.CreateLogger<UrlRunner>(),
			new StaticRoleSingleInstanceGuard(role),
			new NoOpBrowserDetector(),
			new NoOpPicker());
		SafeModeRunner safeModeRunner = new(
			loggerFactory.CreateLogger<SafeModeRunner>(),
			new UnavailableSafeModeRecoveryActionsProvider("the platform recovery implementation is not composed"));
		DefaultsRunner defaultsRunner = new(
			loggerFactory.CreateLogger<DefaultsRunner>(),
			new NoOpCapabilityRegistrationService(),
			new NoOpDefaultAppsStatusService(),
			new NoOpDefaultAppsGuide());

		return new Bootstrap(
			loggerFactory.CreateLogger<Bootstrap>(),
			parser,
			urlRunner,
			safeModeRunner,
			defaultsRunner);
	}

	private static ConsoleRun RunWithConsoleCapture(Action action)
	{
		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		StringWriter stdout = new();
		StringWriter stderr = new();

		try
		{
			Console.SetOut(stdout);
			Console.SetError(stderr);
			action();
			return new ConsoleRun(0, stdout.ToString(), stderr.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	private static async Task<ConsoleRun> RunWithConsoleCaptureAsync(Func<Task<int>> action)
	{
		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		StringWriter stdout = new();
		StringWriter stderr = new();

		try
		{
			Console.SetOut(stdout);
			Console.SetError(stderr);
			int exitCode = await action();
			return new ConsoleRun(exitCode, stdout.ToString(), stderr.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	private static string ReadRepoFile(params string[] relativeSegments)
	{
		string path = Path.Combine(new[] { GetRepositoryRoot() }.Concat(relativeSegments).ToArray());
		return File.ReadAllText(path);
	}

	private static string GetRepositoryRoot()
	{
		DirectoryInfo? current = new(AppContext.BaseDirectory);

		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "BroChoos.sln")))
			{
				return current.FullName;
			}

			current = current.Parent;
		}

		throw new InvalidOperationException("Could not locate BroChoos.sln from test execution directory.");
	}

	private sealed record ConsoleRun(int ExitCode, string StandardOutput, string StandardError);

	private sealed class PassThroughUrlValidator : IUrlValidator
	{
		public bool IsAcceptable(string raw, out string normalized)
		{
			if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
				&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
			{
				normalized = uri.AbsoluteUri;
				return true;
			}

			normalized = string.Empty;
			return false;
		}
	}

	private sealed class StaticRoleSingleInstanceGuard(SingleInstanceRole role) : ISingleInstanceGuard
	{
		private readonly SingleInstanceRole _role = role;

		public Task<SingleInstanceRole> AcquireOrForwardAsync(ActivationRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(_role);
		}
	}

	private sealed class NoOpBrowserDetector : IBrowserDetector
	{
		public IReadOnlyList<BrowserCandidate> Detect()
		{
			return [];
		}
	}

	private sealed class NoOpPicker : IPicker
	{
		public Task<int> ShowAsync(PickerRequest request, CancellationToken cancellationToken)
		{
			return Task.FromResult(ExitCodes.Success);
		}
	}

	private sealed class NoOpCapabilityRegistrationService : ICapabilityRegistrationService
	{
		public CapabilityRegistrationResult RegisterAll()
		{
			return new CapabilityRegistrationResult(true, [], [], null, null);
		}

		public CapabilityRegistrationResult UnregisterAll()
		{
			return new CapabilityRegistrationResult(true, [], [], null, null);
		}
	}

	private sealed class NoOpDefaultAppsStatusService : IDefaultAppsStatusService
	{
		public IReadOnlyList<DefaultAppsEntryStatus> GetStatus()
		{
			return [];
		}
	}

	private sealed class NoOpDefaultAppsGuide : IDefaultAppsGuide
	{
		public bool TryOpenDefaultAppsPage()
		{
			return true;
		}
	}
}
