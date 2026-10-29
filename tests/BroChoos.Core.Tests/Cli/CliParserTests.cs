using BroChoos.Core.Abstractions;
using BroChoos.Core.Cli;
using FluentAssertions;
using Xunit;

namespace BroChoos.Core.Tests.Cli;

public sealed class CliParserTests
{
	[Fact]
	public void Parse_EmptyArgs_ReturnsNoUrlAndNoFlags()
	{
		CliParser parser = CreateParser();
		ParsedArgs result = parser.Parse([]);

		_ = result.HasUrl.Should().BeFalse();
		_ = result.SafeMode.Should().BeFalse();
		_ = result.ShowHelp.Should().BeFalse();
	}

	[Fact]
	public void Parse_SafeModeFlag_SetsSafeMode()
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["--safe-mode"]);

		_ = result.SafeMode.Should().BeTrue();
	}

	[Fact]
	public void Parse_ValidUrl_SetsUrl()
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["https://example.com"]);

		_ = result.HasUrl.Should().BeTrue();
		_ = result.Url.Should().Be("https://example.com/");
	}

	[Fact]
	public void Parse_InvalidUrl_Throws()
	{
		CliParser parser = CreateParser(urlValid: false);

		Func<ParsedArgs> act = () => parser.Parse(["javascript:alert(1)"]);

		_ = act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Parse_UnknownOption_Throws()
	{
		CliParser parser = CreateParser();

		Func<ParsedArgs> act = () => parser.Parse(["--unknown-flag"]);

		_ = act.Should().Throw<ArgumentException>();
	}

	// 0A-4: the four default-apps flags parse as plain case-insensitive flags.
	[Theory]
	[InlineData("--register")]
	[InlineData("--unregister")]
	[InlineData("--verify-defaults")]
	[InlineData("--open-default-apps")]
	[InlineData("--REGISTER")]
	public void Parse_DefaultsOption_SetsItsFlag(string option)
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse([option]);

		_ = result.SafeMode.Should().BeFalse();
		_ = result.HasUrl.Should().BeFalse();
		_ = result.HasAnyDefaultsAction.Should().BeTrue();
		_ = result.Register.Should().Be(option.Equals("--register", StringComparison.OrdinalIgnoreCase));
		_ = result.Unregister.Should().Be(option.Equals("--unregister", StringComparison.OrdinalIgnoreCase));
		_ = result.VerifyDefaults.Should().Be(option.Equals("--verify-defaults", StringComparison.OrdinalIgnoreCase));
		_ = result.OpenDefaultApps.Should().Be(option.Equals("--open-default-apps", StringComparison.OrdinalIgnoreCase));
	}

	// 0A-4: two default-apps actions at once are rejected (exit 3 through
	// Bootstrap); the message carries no values.
	[Theory]
	[InlineData("--register", "--unregister")]
	[InlineData("--verify-defaults", "--open-default-apps")]
	[InlineData("--unregister", "--verify-defaults")]
	public void Parse_ConflictingDefaultsOptions_Throws(string first, string second)
	{
		CliParser parser = CreateParser();

		Func<ParsedArgs> act = () => parser.Parse([first, second]);

		_ = act.Should().Throw<ArgumentException>().WithMessage("*suppressed*");
	}

	// 0A-4: a default-apps flag together with a URL is a PARSE success (the
	// precedence rules in Bootstrap decide; the value is never rejected here).
	[Fact]
	public void Parse_DefaultsOptionWithUrl_SetsBothWithoutThrowing()
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["https://example.com", "--register"]);

		_ = result.Register.Should().BeTrue();
		_ = result.HasUrl.Should().BeTrue();
	}

	// 0A-4: safe mode records the flag (so the ignored-input warning can name
	// it) but never performs the conflict rejection — the rescue hatch does not
	// throw even for --register --unregister.
	[Fact]
	public void Parse_SafeModeWithConflictingDefaultsOptions_NeverThrows()
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["--safe-mode", "--register", "--unregister"]);

		_ = result.SafeMode.Should().BeTrue();
		_ = result.Register.Should().BeTrue();
		_ = result.Unregister.Should().BeTrue();
	}

	// W8.2: the safe-mode selector takes a SEPARATE value token and stores it RAW;
	// vocabulary validation belongs to the runner, which rejects an unknown id
	// without echoing it.
	[Fact]
	public void Parse_SafeModeActionOption_CapturesRawValue()
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["--safe-mode", "--safe-mode-action", "diagnostics-export"]);

		_ = result.SafeMode.Should().BeTrue();
		_ = result.SafeModeAction.Should().Be("diagnostics-export");
	}

	// The rescue hatch never throws: a malformed selector is simply absent, so the
	// run falls back to the full recovery contract instead of failing to parse.
	[Theory]
	[InlineData("--safe-mode", "--safe-mode-action")]
	[InlineData("--safe-mode", "--safe-mode-action", "--no-ui")]
	[InlineData("--safe-mode", "--safe-mode-action", "   ")]
	public void Parse_MalformedSafeModeAction_NeverThrows(params string[] args)
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(args);

		_ = result.SafeMode.Should().BeTrue();
		_ = result.SafeModeAction.Should().BeNull();
		_ = result.SafeModeActionInvalid.Should().BeTrue("a malformed selector is carried as state, not silently dropped");
	}

	[Fact]
	public void Parse_ValidSafeModeAction_IsNotMarkedInvalid()
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["--safe-mode", "--safe-mode-action", "diagnostics-export"]);

		_ = result.SafeModeActionInvalid.Should().BeFalse();
	}

	// Outside safe mode the selector selects nothing: it used to be parsed, ignored, and
	// answered with help + NoArgs, so a user who forgot --safe-mode got no error at all.
	// It is now a malformed command line (ArgumentException -> exit 3), and the message
	// names the missing flag without echoing the value.
	[Theory]
	[InlineData("--safe-mode-action", "reset-defaults")]
	[InlineData("--SAFE-MODE-ACTION", "reset-defaults")]
	[InlineData("https://example.com", "--safe-mode-action", "reset-defaults")]
	[InlineData("--settings", "--safe-mode-action", "reset-defaults")]
	[InlineData("--safe-mode-action", "reset-defaults", "--settings")]
	public void Parse_SafeModeActionWithoutSafeMode_IsRejected(params string[] args)
	{
		CliParser parser = CreateParser();

		Action act = () => parser.Parse(args);

		_ = act.Should().Throw<ArgumentException>()
			.WithMessage("--safe-mode-action requires --safe-mode*")
			.Which.Message.Should().NotContain("reset-defaults");
	}

	// 0A-5 (D-v3.5-072): --portable-data takes its value as a SEPARATE token, and the
	// value is stored RAW — the parser neither normalizes nor resolves it.
	[Fact]
	public void Parse_PortableDataOptionWithUrl_CapturesRawValue()
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["--portable-data", @"D:\BroChoosData", "https://example.com"]);

		_ = result.PortableDataPath.Should().Be(@"D:\BroChoosData");
		_ = result.HasUrl.Should().BeTrue();
		_ = result.Url.Should().Be("https://example.com/");
	}

	// 0A-5: "RAW" pinned against the three normalizations the resolver performs, because
	// the value above (@"D:\BroChoosData") is already absolute, already trimmed and has no
	// trailing separator — so all three would be no-ops on it and none of them would be
	// noticed. Each value below DIFFERS from its own normalized form: relative
	// (Path.GetFullPath would rewrite it), trailing separator
	// (Path.TrimEndingDirectorySeparator would strip it), surrounding spaces (Trim would
	// remove them). StorageRootResolver.ResolveRoot owns all three; if the parser did any
	// of them first, that resolver's tests would be measuring the parser's output instead
	// of their own subject.
	[Theory]
	[InlineData("BroChoosConfig")]
	[InlineData(@"D:\BroChoosData\")]
	[InlineData(@"  D:\BroChoosData  ")]
	public void Parse_PortableDataValue_IsStoredExactlyAsTyped(string value)
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["--portable-data", value]);

		_ = result.PortableDataPath.Should().Be(value);
	}

	// 0A-5: the parser is PURE — it stores the value and touches no disk: no existence
	// probe, no directory creation. Provisioning belongs to StorageRootResolver (which
	// eagerly creates Root/backups/logs/temp), and a parser that provisioned would leave
	// a directory behind for a command line that is rejected two tokens later. The
	// candidate lives under the temp directory and does not exist, so its appearance is
	// the whole assertion.
	[Fact]
	public void Parse_PortableDataOption_CreatesNothingOnDisk()
	{
		string candidate = Path.Combine(Path.GetTempPath(), $"brochoos-parser-purity-{Guid.NewGuid():N}");
		CliParser parser = CreateParser();

		try
		{
			ParsedArgs result = parser.Parse(["--portable-data", candidate]);

			_ = result.PortableDataPath.Should().Be(candidate);
			_ = Directory.Exists(candidate).Should().BeFalse();
			_ = File.Exists(candidate).Should().BeFalse();
		}
		finally
		{
			// Reachable only if a regression provisioned it; leaves temp clean either way.
			if (Directory.Exists(candidate))
			{
				Directory.Delete(candidate, recursive: true);
			}
		}
	}

	// 0A-5: the long option name is matched case-insensitively, like every other
	// long option in this parser.
	[Theory]
	[InlineData("--PORTABLE-DATA")]
	[InlineData("--Portable-Data")]
	public void Parse_PortableDataOptionNameCasing_IsIgnored(string option)
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse([option, @"D:\BroChoosData"]);

		_ = result.PortableDataPath.Should().Be(@"D:\BroChoosData");
	}

	// 0A-5: repetition is tolerated exactly like the other flags (the parser has
	// never rejected a duplicate); the LAST occurrence wins.
	[Fact]
	public void Parse_PortableDataOptionRepeated_LastOccurrenceWins()
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["--portable-data", @"D:\First", "--portable-data", @"E:\Second"]);

		_ = result.PortableDataPath.Should().Be(@"E:\Second");
	}

	// 0A-5: the value check is deliberately NOT "starts with a dash". The parser's
	// notion of an option is the "--"/"/" prefixes plus the -h synonym, so a
	// single-dash token that is none of those is a legitimate directory name and is
	// taken as the value ("-h" itself is the exception, and is pinned as REJECTED in
	// the theory below). The trailing URL is the load-bearing half of each row: it
	// can only parse if the loop advanced PAST the value, instead of re-reading the
	// dash-leading token as an unknown option.
	//
	// The asymmetry this creates is intended, not an oversight: the same "-x" as a
	// POSITIONAL is refused, because the real validator rejects every dash-leading
	// token. That half cannot be measured here — the fake validator below accepts
	// whatever it is given — so it is pinned on the validator itself, in
	// DefaultUrlValidatorTests.
	[Theory]
	[InlineData("-x")]
	[InlineData("-")]
	[InlineData("-portable-data")]
	public void Parse_PortableDataValueBeginningWithASingleDash_IsTakenAsTheValue(string value)
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["--portable-data", value, "https://example.com"]);

		_ = result.PortableDataPath.Should().Be(value);
		_ = result.Url.Should().Be("https://example.com/");
	}

	// 0A-5: a missing value is a malformed command line, rejected through the SAME
	// mechanism as every other bad argument (ArgumentException -> exit 3 in
	// Bootstrap). "Missing" is either no next token at all, or a next token that
	// starts another option — including the single-dash synonym -h and any
	// /-prefixed token, both of which this parser treats as options (IsHelpOption
	// covers -h and /?; the leading-/ prefix covers the rest, exactly as the
	// unknown-option branch does). The /-prefixed cases beyond /? are listed
	// separately on purpose: /? alone is also matched by IsHelpOption, so it cannot
	// notice the loss of the prefix check, and without them "--portable-data
	// /register" would silently swallow a flag as a data root.
	[Theory]
	[InlineData("--portable-data")]
	[InlineData("https://example.com", "--portable-data")]
	[InlineData("--portable-data", "--no-ui", "https://example.com")]
	[InlineData("--portable-data", "--help")]
	[InlineData("--portable-data", "-h")]
	[InlineData("--portable-data", "/?")]
	[InlineData("--portable-data", "/unknown-option")]
	[InlineData("--portable-data", "/")]
	[InlineData("--portable-data", "--portable-data", @"D:\BroChoosData")]
	public void Parse_PortableDataOptionWithoutValue_Throws(params string[] args)
	{
		CliParser parser = CreateParser();

		Func<ParsedArgs> act = () => parser.Parse(args);

		ArgumentException thrown = act.Should().Throw<ArgumentException>().WithMessage("*suppressed*").Which;
		// Privacy: a path is the user's data. Even when a later token in the same
		// command line is a path, the message must not carry it.
		_ = thrown.Message.Should().NotContain(@"D:\BroChoosData");
	}

	// 0A-5: an empty or whitespace-only value is rejected by the same mechanism.
	// Not accepted-then-ignored: a blank value would fall through to the next
	// storage-mode priority silently, which is not what the user asked for.
	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("\t")]
	[InlineData("   ")]
	public void Parse_PortableDataOptionWithBlankValue_Throws(string value)
	{
		CliParser parser = CreateParser();

		Func<ParsedArgs> act = () => parser.Parse(["--portable-data", value, "https://example.com"]);

		ArgumentException thrown = act.Should().Throw<ArgumentException>().WithMessage("*suppressed*").Which;
		// Same privacy rule as the missing-value rejection, asserted separately because
		// it is a separate message: no token of the command line may travel in it. The
		// blank value itself carries nothing, so the URL two positions along is the
		// value actually at risk here.
		_ = thrown.Message.Should().NotContain("example.com");
	}

	// 0A-5: the "=" spelling is NOT this option. It must behave exactly like any
	// other unrecognized --flag, so the message is compared against the one the
	// pre-existing unknown-option path produces rather than to a literal.
	[Theory]
	[InlineData(@"--portable-data=D:\BroChoosData")]
	[InlineData("--portable-data=")]
	[InlineData(@"--PORTABLE-DATA=D:\BroChoosData")]
	public void Parse_PortableDataWithEqualsForm_IsTreatedAsUnknownOption(string arg)
	{
		CliParser parser = CreateParser();
		string unknownOptionMessage = ((Func<ParsedArgs>)(() => parser.Parse(["--definitely-not-an-option"])))
			.Should().Throw<ArgumentException>().Which.Message;

		Func<ParsedArgs> act = () => parser.Parse([arg, "https://example.com"]);

		ArgumentException thrown = act.Should().Throw<ArgumentException>().Which;
		_ = thrown.Message.Should().Be(unknownOptionMessage);
		_ = thrown.Message.Should().NotContain(@"D:\BroChoosData");
	}

	// W8.2 CHANGED THIS BEHAVIOUR, and the change is the point of the task. Before
	// W8.2 safe mode ignored --portable-data entirely (the placeholder performed no
	// action, so it had no reason to resolve a root). Safe Mode recovery actions now
	// DO operate on the data folder, so the rescue hatch must carry the root through.
	// What must NOT change is the no-throw property: a malformed value is still
	// ignored rather than rejected.
	[Fact]
	public void Parse_SafeModeWithPortableDataOption_RetainsTheValueForRecovery()
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(["--safe-mode", "--portable-data", @"D:\BroChoosData"]);

		_ = result.SafeMode.Should().BeTrue();
		_ = result.PortableDataPath.Should().Be(@"D:\BroChoosData");
		_ = result.IgnoredUnknownOptionCount.Should().Be(0);
		_ = result.IgnoredPositionalCount.Should().Be(0);
	}

	// 0A-5: the rescue hatch does not throw on the malformed forms either — the
	// strict-path rejections must not leak into safe mode.
	[Theory]
	[InlineData("--safe-mode", "--portable-data")]
	[InlineData("--portable-data", "--safe-mode")]
	[InlineData("--safe-mode", "--portable-data", "")]
	[InlineData("--safe-mode", "--portable-data", " ")]
	[InlineData("--safe-mode", @"--portable-data=D:\BroChoosData")]
	public void Parse_SafeModeWithMalformedPortableData_NeverThrowsAndRetainsNoValue(params string[] args)
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse(args);

		_ = result.SafeMode.Should().BeTrue();
		_ = result.PortableDataPath.Should().BeNull();
	}

	// W8.1: --settings opens the Settings shell. It is a plain case-insensitive flag, and
	// it is its OWN operating mode, so combining it with anything that would run another
	// mode is rejected rather than silently picking one.
	[Theory]
	[InlineData("--settings")]
	[InlineData("--SETTINGS")]
	public void Parse_SettingsOption_SetsSettingsFlag(string option)
	{
		CliParser parser = CreateParser();

		ParsedArgs result = parser.Parse([option]);

		_ = result.Settings.Should().BeTrue();
		_ = result.SafeMode.Should().BeFalse();
		_ = result.HasUrl.Should().BeFalse();
	}

	[Theory]
	[InlineData("--settings", "https://example.com")]
	[InlineData("--settings", "--register")]
	[InlineData("--settings", "--no-ui")]
	[InlineData("--settings", "--portable-data", @"D:\BroChoosData")]
	public void Parse_SettingsCombinedWithAnotherOperatingMode_Throws(params string[] args)
	{
		CliParser parser = CreateParser();

		Func<ParsedArgs> act = () => parser.Parse(args);

		_ = act.Should().Throw<ArgumentException>();
	}

	private static CliParser CreateParser(bool urlValid = true)
	{
		return new CliParser(new FakeUrlValidator(urlValid));
	}

	private sealed class FakeUrlValidator(bool urlValid) : IUrlValidator
	{
		private readonly bool _urlValid = urlValid;

		public bool IsAcceptable(string raw, out string normalized)
		{
			normalized = _urlValid && Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
				? uri.AbsoluteUri
				: string.Empty;

			return _urlValid;
		}
	}
}
