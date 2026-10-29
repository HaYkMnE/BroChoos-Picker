using System.Diagnostics;
using BroChoos.Core.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PlatformBrowsers = BroChoos.Platform.Windows.Browsers;

namespace BroChoos.Platform.Windows.Tests.Browsers;

// D-130's F-key surface: the shell association is invoked with the URL as FileName
// and nothing else ever carries it - no Arguments string, no ArgumentList entry,
// so there is no command line a URL could be reparsed into (D-183/D-199/D-310
// invariants hold here too). Outcomes are LaunchResults, never escaping exceptions,
// and log lines stay URL-free (privacy rule).
public sealed class SystemFallbackLauncherTests
{
	private const string Url = "https://example.com/search?q=1";

	[Fact]
	public void Launch_BuildsShellExecuteRequest_WithoutAnyArgumentPayload()
	{
		CapturingProcessStarter starter = new() { Result = true };
		PlatformBrowsers.SystemFallbackLauncher sut = NewLauncher(starter);

		LaunchResult result = sut.Launch(Url);

		_ = result.Status.Should().Be(LaunchStatus.Success);
		_ = result.TargetId.Should().Be(PlatformBrowsers.SystemFallbackLauncher.SystemTargetId);
		_ = starter.StartCount.Should().Be(1);
		_ = starter.Received.Should().NotBeNull();
		_ = starter.Received!.UseShellExecute.Should().BeTrue();
		_ = starter.Received.FileName.Should().Be(Url);
		_ = starter.Received.Arguments.Should().BeEmpty();
		_ = starter.Received.ArgumentList.Should().BeEmpty();
	}

	[Fact]
	public void Launch_StarterRefuses_ReturnsFailedLaunchResult()
	{
		CapturingProcessStarter starter = new() { Result = false };
		PlatformBrowsers.SystemFallbackLauncher sut = NewLauncher(starter);

		LaunchResult result = sut.Launch(Url);

		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.TargetId.Should().Be(PlatformBrowsers.SystemFallbackLauncher.SystemTargetId);
		_ = result.ErrorCode.Should().Be(PlatformBrowsers.SystemFallbackLauncher.ErrorStartFailed);
		_ = result.FailureCategory.Should().Be(PlatformBrowsers.SystemFallbackLauncher.CategoryProcessStart);
	}

	[Fact]
	public void Launch_StarterThrows_ReturnsFailedLaunchResultWithoutEscaping()
	{
		CapturingProcessStarter starter = new() { Throws = new InvalidOperationException("simulated shell failure") };
		PlatformBrowsers.SystemFallbackLauncher sut = NewLauncher(starter);

		LaunchResult result = sut.Launch(Url);

		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.ErrorCode.Should().Be(PlatformBrowsers.SystemFallbackLauncher.ErrorStartFailed);
	}

	[Fact]
	public void Launch_EmptyUrl_FailsWithoutStartingAnything()
	{
		CapturingProcessStarter starter = new() { Result = true };
		PlatformBrowsers.SystemFallbackLauncher sut = NewLauncher(starter);

		LaunchResult result = sut.Launch(" ");

		_ = result.Status.Should().Be(LaunchStatus.Failed);
		_ = result.ErrorCode.Should().Be(PlatformBrowsers.SystemFallbackLauncher.ErrorInvalidUrl);
		_ = starter.StartCount.Should().Be(0);
	}

	// The URL is the user's own input; persistent log lines must never carry it.
	[Fact]
	public void Launch_NeverWritesTheUrlToLogs()
	{
		ListLogger logger = new();
		CapturingProcessStarter starter = new() { Throws = new InvalidOperationException("simulated shell failure") };
		PlatformBrowsers.SystemFallbackLauncher sut = new(logger, starter);

		_ = sut.Launch(Url);

		_ = starter.StartCount.Should().Be(1);
		_ = logger.Entries.Should().NotBeEmpty();
		_ = logger.Entries.Should().OnlyContain(e => !e.Contains("example.com", StringComparison.Ordinal));
	}

	private static PlatformBrowsers.SystemFallbackLauncher NewLauncher(CapturingProcessStarter starter)
	{
		return new PlatformBrowsers.SystemFallbackLauncher(NullLogger<PlatformBrowsers.SystemFallbackLauncher>.Instance, starter);
	}

	private sealed class CapturingProcessStarter : PlatformBrowsers.IProcessStarter
	{
		public bool Result { get; set; }

		public Exception? Throws { get; set; }

		public int StartCount { get; private set; }

		public ProcessStartInfo? Received { get; private set; }

		public bool Start(ProcessStartInfo startInfo)
		{
			StartCount++;
			Received = startInfo;
			return Throws is not null ? throw Throws : Result;
		}
	}

	// Captures every level so log-redaction can be asserted, not just warnings.
	private sealed class ListLogger : ILogger<PlatformBrowsers.SystemFallbackLauncher>
	{
		private readonly List<string> _entries = [];

		public IReadOnlyList<string> Entries => _entries;

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
			_entries.Add(formatter(state, exception));
		}
	}
}
