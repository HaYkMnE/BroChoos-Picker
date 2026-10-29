using BroChoos.Core.Abstractions;
using BroChoos.Core.Runners;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroChoos.Core.Tests.Runners;

// Redirects Console.Out/In/Error; see ConsoleCaptureCollection.
[Collection(ConsoleCaptureCollection.Name)]
public sealed class DefaultsRunnerTests
{
	[Fact]
	public void Register_Success_PrintsBackupAndPerTypeStatus_ReturnsZero()
	{
		RunnerFixture fixture = new();
		fixture.Registration.NextRegister = new CapabilityRegistrationResult(
			true,
			[Outcome(DefaultAppsTarget.Http, "http", true), Outcome(DefaultAppsTarget.Https, "https", true), Outcome(DefaultAppsTarget.UrlFile, ".url", true)],
			[],
			@"C:\Users\dev\AppData\Local\BroChoos\backups\registration-1.json",
			null);

		TestConsole console = TestConsole.Capture();
		try
		{
			int exit = fixture.Runner.Register();

			_ = exit.Should().Be(ExitCodes.Success);
			_ = console.Out.Should().Contain("Backup of the pre-write state:");
			// Per-type Registered status is printed AFTER writing, as a table row per type.
			_ = console.Out.Should().Contain("registered");
			_ = console.Out.Should().Contain("http");
			_ = console.Out.Should().Contain("https");
			_ = console.Out.Should().Contain(".url");
			_ = console.Out.Should().Contain("The default is NOT changed");
			// The verification table is repopulated from the status service, not from
			// what the write pass claims it wrote.
			_ = fixture.Status.GetStatusCalls.Should().Be(1);
		}
		finally
		{
			console.Restore();
		}
	}

	[Fact]
	public void Register_Failure_ReturnsGeneralErrorWithHonestMessage()
	{
		RunnerFixture fixture = new();
		fixture.Registration.NextRegister = new CapabilityRegistrationResult(false, [], [], null, "synthetic failure");

		TestConsole console = TestConsole.Capture();
		try
		{
			int exit = fixture.Runner.Register();

			_ = exit.Should().Be(ExitCodes.GeneralError);
			_ = console.Error.Should().Contain("Registration failed");
		}
		finally
		{
			console.Restore();
		}
	}

	[Fact]
	public void Unregister_Success_PrintsRemovedBackupAndRestorePath_ReturnsZero()
	{
		RunnerFixture fixture = new();
		fixture.Registration.NextUnregister = new CapabilityRegistrationResult(
			true,
			[Outcome(DefaultAppsTarget.Http, "http", false), Outcome(DefaultAppsTarget.Https, "https", false), Outcome(DefaultAppsTarget.UrlFile, ".url", false)],
			[@"HKCU\Software\RegisteredApplications (value ""BroChoos"")", @"HKCU\Software\Classes\BroChoosURL (key tree)"],
			@"C:\Users\dev\AppData\Local\BroChoos\backups\registration-2.json",
			null);

		TestConsole console = TestConsole.Capture();
		try
		{
			int exit = fixture.Runner.Unregister();

			_ = exit.Should().Be(ExitCodes.Success);
			_ = console.Out.Should().Contain("Removed: HKCU\\Software\\RegisteredApplications");
			_ = console.Out.Should().Contain("Removed: HKCU\\Software\\Classes\\BroChoosURL");
			_ = console.Out.Should().Contain("Restore path:");
			_ = console.Out.Should().Contain("registration-2.json");
		}
		finally
		{
			console.Restore();
		}
	}

	[Fact]
	public void Unregister_Failure_ReturnsGeneralError()
	{
		RunnerFixture fixture = new();
		fixture.Registration.NextUnregister = new CapabilityRegistrationResult(false, [], [], null, "synthetic failure");

		TestConsole console = TestConsole.Capture();
		try
		{
			int exit = fixture.Runner.Unregister();

			_ = exit.Should().Be(ExitCodes.GeneralError);
			_ = console.Error.Should().Contain("Unregister failed");
		}
		finally
		{
			console.Restore();
		}
	}

	[Fact]
	public void VerifyDefaults_PrintsTheThreeStepTable_WithAllSelectionKinds()
	{
		RunnerFixture fixture = new();
		fixture.Status.Entries =
		[
			new DefaultAppsEntryStatus(DefaultAppsTarget.Http, "http", "BroChoosURL", true, "BroChoosURL", "no activation recorded yet"),
			new DefaultAppsEntryStatus(DefaultAppsTarget.Https, "https", "BroChoosURL", true, "ChromeHTML", "no activation recorded yet"),
			new DefaultAppsEntryStatus(DefaultAppsTarget.UrlFile, ".url", "BroChoosURL.File", false, null, "no activation recorded yet"),
		];

		TestConsole console = TestConsole.Capture();
		try
		{
			int exit = fixture.Runner.VerifyDefaults();

			_ = exit.Should().Be(ExitCodes.Success);
			_ = console.Out.Should().Contain("target | registered | selected (UserChoice ProgId) | handled");
			// Step 2 renders the three honest kinds: ours, another app, no choice.
			_ = console.Out.Should().Contain("BroChoos (BroChoosURL)");
			_ = console.Out.Should().Contain("other app (ChromeHTML)");
			_ = console.Out.Should().Contain("(no choice recorded)");
			// Step 3 is always the honest note in this milestone.
			_ = console.Out.Should().Contain("no activation recorded yet");
		}
		finally
		{
			console.Restore();
		}
	}

	[Fact]
	public void VerifyDefaults_NotRegisteredAnywhere_StillReturnsZeroAndPointsToRegister()
	{
		RunnerFixture fixture = new();

		TestConsole console = TestConsole.Capture();
		try
		{
			int exit = fixture.Runner.VerifyDefaults();

			_ = exit.Should().Be(ExitCodes.Success);
			_ = console.Out.Should().Contain("not registered");
			_ = console.Out.Should().Contain("--register");
		}
		finally
		{
			console.Restore();
		}
	}

	// A hostile UserChoice ProgId (HKCU-writable) must be scrubbed and capped
	// before it reaches the console: control characters become '?' and the value
	// is cut to a bounded length.
	[Fact]
	public void VerifyDefaults_HostileSelectedProgId_IsScrubbedAndCapped()
	{
		RunnerFixture fixture = new();
		string hostile = "Evil\nSet-Content" + new string('A', 500);
		fixture.Status.Entries =
		[
			new DefaultAppsEntryStatus(DefaultAppsTarget.Http, "http", "BroChoosURL", false, hostile, "no activation recorded yet"),
			new DefaultAppsEntryStatus(DefaultAppsTarget.Https, "https", "BroChoosURL", false, null, "no activation recorded yet"),
			new DefaultAppsEntryStatus(DefaultAppsTarget.UrlFile, ".url", "BroChoosURL.File", false, null, "no activation recorded yet"),
		];

		TestConsole console = TestConsole.Capture();
		try
		{
			int exit = fixture.Runner.VerifyDefaults();

			_ = exit.Should().Be(ExitCodes.Success);
			// The cap is 64 chars; a control character inside the window becomes '?'.
			_ = console.Out.Should().Contain("other app (Evil?Set-Content");
			_ = console.Out.Should().Contain("...");
			_ = console.Out.Should().NotContain(hostile);
		}
		finally
		{
			console.Restore();
		}
	}

	[Fact]
	public void OpenDefaultAppsGuide_PrintsTheRussianGuide_OpensThePage_AndReRunsStatus()
	{
		RunnerFixture fixture = new();

		TestConsole console = TestConsole.Capture();
		try
		{
			int exit = fixture.Runner.OpenDefaultAppsGuide();

			_ = exit.Should().Be(ExitCodes.Success);
			_ = fixture.Guide.OpenCalls.Should().Be(1);
			// Verification re-runs after the (simulated) user action.
			_ = fixture.Status.GetStatusCalls.Should().Be(1);
			// The guide speaks Russian, and never promises to flip the default itself.
			_ = console.Out.Should().Contain("BroChoos");
			_ = console.Out.Should().Contain("умолчан");
			_ = console.Out.Should().Contain("Status after your choice:");
		}
		finally
		{
			console.Restore();
		}
	}

	// Fail-open: an unopenable settings page degrades to the text guide; the flow
	// still ends with a fresh verification table and exit 0.
	[Fact]
	public void OpenDefaultAppsGuide_PageCannotOpen_DegradesToPrintOnlyAndReturnsZero()
	{
		RunnerFixture fixture = new();
		fixture.Guide.OpenResult = false;

		TestConsole console = TestConsole.Capture();
		try
		{
			int exit = fixture.Runner.OpenDefaultAppsGuide();

			_ = exit.Should().Be(ExitCodes.Success);
			_ = console.Error.Should().Contain("could not be opened");
			_ = console.Out.Should().Contain("Status after your choice:");
			_ = fixture.Status.GetStatusCalls.Should().Be(1);
		}
		finally
		{
			console.Restore();
		}
	}

	private static RegisteredTypeOutcome Outcome(DefaultAppsTarget target, string scheme, bool registered)
	{
		return new RegisteredTypeOutcome(target, scheme, registered, null);
	}

	// --- Fixture ---------------------------------------------------------------

	private sealed class RunnerFixture
	{
		public RunnerFixture()
		{
			Runner = new DefaultsRunner(NullLogger<DefaultsRunner>.Instance, Registration, Status, Guide);
		}

		public DefaultsRunner Runner { get; }

		public FakeRegistration Registration { get; } = new();

		public FakeStatus Status { get; } = new();

		public FakeGuide Guide { get; } = new();

		private sealed class NullLogger<T> : ILogger<T>
		{
			internal static readonly NullLogger<T> Instance = new();

			public IDisposable? BeginScope<TState>(TState state)
				where TState : notnull
			{
				return null;
			}

			public bool IsEnabled(LogLevel logLevel)
			{
				return false;
			}

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
			}
		}
	}

	private sealed class FakeRegistration : ICapabilityRegistrationService
	{
		public CapabilityRegistrationResult NextRegister { get; set; } = new(true, [], [], null, null);

		public CapabilityRegistrationResult NextUnregister { get; set; } = new(true, [], [], null, null);

		public CapabilityRegistrationResult RegisterAll()
		{
			return NextRegister;
		}

		public CapabilityRegistrationResult UnregisterAll()
		{
			return NextUnregister;
		}
	}

	private sealed class FakeStatus : IDefaultAppsStatusService
	{
		public int GetStatusCalls { get; private set; }

		public IReadOnlyList<DefaultAppsEntryStatus> Entries { get; set; } =
		[
			new DefaultAppsEntryStatus(DefaultAppsTarget.Http, "http", "BroChoosURL", false, null, "no activation recorded yet"),
			new DefaultAppsEntryStatus(DefaultAppsTarget.Https, "https", "BroChoosURL", false, null, "no activation recorded yet"),
			new DefaultAppsEntryStatus(DefaultAppsTarget.UrlFile, ".url", "BroChoosURL.File", false, null, "no activation recorded yet"),
		];

		public IReadOnlyList<DefaultAppsEntryStatus> GetStatus()
		{
			GetStatusCalls++;
			return Entries;
		}
	}

	private sealed class FakeGuide : IDefaultAppsGuide
	{
		public bool OpenResult { get; set; } = true;

		public int OpenCalls { get; private set; }

		public bool TryOpenDefaultAppsPage()
		{
			OpenCalls++;
			return OpenResult;
		}
	}

	// Captures AND restores all console channels; input is replaced by an empty
	// reader so a runner prompt can never block the suite.
	private sealed class TestConsole : IDisposable
	{
		private readonly TextWriter _originalOut;
		private readonly TextWriter _originalError;
		private readonly TextReader _originalIn;
		private readonly StringWriter _out;
		private readonly StringWriter _error;

		private TestConsole()
		{
			_originalOut = Console.Out;
			_originalError = Console.Error;
			_originalIn = Console.In;
			_out = new StringWriter();
			_error = new StringWriter();
		}

		internal string Out => _out.ToString();

		internal string Error => _error.ToString();

		internal static TestConsole Capture()
		{
			TestConsole captured = new();
			Console.SetOut(captured._out);
			Console.SetError(captured._error);
			Console.SetIn(new StringReader(string.Empty));
			return captured;
		}

		internal void Restore()
		{
			Console.SetOut(_originalOut);
			Console.SetError(_originalError);
			Console.SetIn(_originalIn);
		}

		public void Dispose()
		{
			_out.Dispose();
			_error.Dispose();
		}
	}
}
