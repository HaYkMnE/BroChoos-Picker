using BroChoos.Core.Abstractions;

namespace BroChoos.Core.Cli;

public sealed class CliParser(IUrlValidator urlValidator)
{
	private readonly IUrlValidator _urlValidator = urlValidator;

	public ParsedArgs Parse(string[] args)
	{
		// Safe mode is the recovery hatch: it must take precedence even over a malformed
		// invocation, so the flag is checked before any URL validation can reject input.
		return args.Any(IsSafeModeOption) ? ParseSafeMode(args) : ParseStrict(args);
	}

	private ParsedArgs ParseStrict(string[] args)
	{
		ParsedArgs result = new();

		for (int index = 0; index < args.Length; index++)
		{
			string arg = args[index];

			// Option matching is case-insensitive; synonyms are restricted to the locked
			// spec set (0A-1: --safe-mode, --no-ui, --help/-h//?; 0A-4 adds the four
			// default-apps actions below, each a plain flag with no synonyms; 0A-5 adds
			// --portable-data, the only option that takes a value).
			if (IsSafeModeOption(arg))
			{
				result.SafeMode = true;
			}
			else if (IsNoUiOption(arg))
			{
				result.NoUi = true;
			}
			else if (IsHelpOption(arg))
			{
				result.ShowHelp = true;
			}
			else if (IsRegisterOption(arg))
			{
				result.Register = true;
			}
			else if (IsUnregisterOption(arg))
			{
				result.Unregister = true;
			}
			else if (IsVerifyDefaultsOption(arg))
			{
				result.VerifyDefaults = true;
			}
			else if (IsOpenDefaultAppsOption(arg))
			{
				result.OpenDefaultApps = true;
			}
			else if (IsPortableDataOption(arg))
			{
				// Separate-token value only (D-v3.5-072 spells the option as
				// "--portable-data <path>"). The value is stored RAW: normalization and
				// provisioning belong to whoever resolves the storage root.
				result.PortableDataPath = TakePortableDataValue(args, ref index);
			}
			else if (IsSafeModeActionOption(arg))
			{
				// W8.2: `--safe-mode-action <id>` selects ONE recovery route. The value
				// is stored RAW; the runner rejects an unknown id without echoing it.
				result.SafeModeAction = TakeSafeModeActionValue(args, ref index);
			}
			else if (IsSettingsOption(arg))
			{
				// W8.1: `--settings` opens the Settings shell. A UI mode like any other;
				// it takes no value and is validated for conflicts below.
				result.Settings = true;
			}
			else if (arg.StartsWith("--", StringComparison.Ordinal) || arg.StartsWith('/'))
			{
				throw new ArgumentException("Unknown option (value suppressed)");
			}
			else if (result.Url is null)
			{
				if (!_urlValidator.IsAcceptable(arg, out string? normalized))
				{
					// Never embed the raw value: rejected input is the user's URL, and
					// Bootstrap logs exception messages.
					throw new ArgumentException("Invalid URL (value suppressed)");
				}

				result.Url = normalized;
			}
			else
			{
				// Same privacy rule: a second positional argument would be another URL.
				throw new ArgumentException("Unexpected positional argument (value suppressed)");
			}
		}

		// One operating mode per invocation: two default-apps actions at once (for
		// example --register --unregister) is a conflicting request, mapped to the
		// same rejection code as any other malformed command line (exit 3).
		int defaultsActionCount = (result.Register ? 1 : 0)
			+ (result.Unregister ? 1 : 0)
			+ (result.VerifyDefaults ? 1 : 0)
			+ (result.OpenDefaultApps ? 1 : 0);
		if (defaultsActionCount > 1)
		{
			throw new ArgumentException("Conflicting default-apps options (details suppressed)");
		}

		// W8.2: --safe-mode-action is a selector INSIDE safe mode. ParseStrict only runs
		// when --safe-mode is absent (Parse's pre-scan), so a selector here selects nothing.
		// Before this check it was parsed, ignored, and answered with help + NoArgs, so a
		// user who forgot --safe-mode was told nothing. Same rejection as any other invalid
		// combination (exit 3); the action value is never carried.
		if (result.SafeModeAction is not null)
		{
			throw new ArgumentException("--safe-mode-action requires --safe-mode (value suppressed)");
		}

		// W8.1: --settings is its own operating mode. Combined with a URL, a default-apps
		// action, --no-ui, or --portable-data it is ambiguous about which mode should run,
		// so it is rejected the same way (exit 3) rather than silently picking one. A
		// malformed command line is a malformed command line.
		return result.Settings && (result.HasUrl || result.HasAnyDefaultsAction || result.NoUi || result.PortableDataPath is not null)
			? throw new ArgumentException("--settings cannot be combined with another operating mode (details suppressed)")
			: result;
	}

	// Recovery mode never validates and never throws: every extra input is only tallied,
	// so the ignored combination can be named in a warning without surfacing values.
	private static ParsedArgs ParseSafeMode(string[] args)
	{
		ParsedArgs result = new() { SafeMode = true };

		// Index-based, because --safe-mode-action consumes a separate value token.
		// The rescue hatch still never throws: a malformed selector is not recorded as
		// an action, but the invalid-selector state is carried (SafeModeActionInvalid) so
		// the run is rejected instead of falling back to the full recovery contract.
		for (int index = 0; index < args.Length; index++)
		{
			string arg = args[index];

			if (IsSafeModeOption(arg))
			{
				// Already captured by the pre-scan; a duplicate flag stays harmless.
			}
			else if (IsNoUiOption(arg))
			{
				result.NoUi = true; // Recorded so the ignored-input tally is honest; since 0A-3 the flag is live on the URL path (console surface instead of the Picker).
			}
			else if (IsHelpOption(arg))
			{
				// Recorded only so the ignored-input warning can name it.
				result.ShowHelp = true;
			}
			else if (IsRegisterOption(arg))
			{
				// Same idea: safe mode wins over every default-apps action, and the flag
				// is recorded only so the ignored-input warning can name it. No conflict
				// check runs here: the rescue hatch never throws.
				result.Register = true;
			}
			else if (IsUnregisterOption(arg))
			{
				result.Unregister = true;
			}
			else if (IsVerifyDefaultsOption(arg))
			{
				result.VerifyDefaults = true;
			}
			else if (IsOpenDefaultAppsOption(arg))
			{
				result.OpenDefaultApps = true;
			}
			else if (IsSafeModeActionOption(arg))
			{
				// W8.2: safe mode takes precedence, so the selector is recorded only when
				// a usable value follows. A missing/option-like/blank value is NOT a parse
				// failure here (the rescue hatch must never throw), but it is not silently
				// dropped either: the invalid-selector state is carried so Bootstrap can
				// fail the run instead of reporting the full contract.
				if (index + 1 < args.Length && !LooksLikeOption(args[index + 1]) && !string.IsNullOrWhiteSpace(args[index + 1]))
				{
					index++;
					result.SafeModeAction = args[index];
				}
				else
				{
					result.SafeModeActionInvalid = true;
				}
			}
			else if (IsPortableDataOption(arg))
			{
				// W8.2: the recovery actions resolve the data root, so Safe Mode must be
				// able to see --portable-data. Recorded RAW and only when a usable value
				// follows; a malformed value is ignored rather than thrown, because the
				// rescue hatch never rejects a command line.
				if (index + 1 < args.Length && !LooksLikeOption(args[index + 1]) && !string.IsNullOrWhiteSpace(args[index + 1]))
				{
					index++;
					result.PortableDataPath = args[index];
				}
			}
			else if (arg.StartsWith("--", StringComparison.Ordinal) || arg.StartsWith('/'))
			{
				result.IgnoredUnknownOptionCount++;
			}
			else
			{
				// Not validated on purpose: garbage must not block the rescue hatch.
				result.IgnoredPositionalCount++;
			}
		}

		return result;
	}

	// 0A-5 (D-v3.5-072). Consumes the SEPARATE value token of --portable-data and
	// advances the loop past it. A value is rejected when the option was the last
	// token, when the next token is itself an option — otherwise
	// "--portable-data --no-ui https://x" would silently take "--no-ui" as a data
	// root — and when it is blank, because a blank value would fall through to the
	// next storage-mode priority instead of honouring what was asked for. All three
	// are one malformed command line: the same ArgumentException every other bad
	// argument raises, and never carrying a value (a data path is the user's data,
	// exactly like the URL).
	private static string TakePortableDataValue(string[] args, ref int index)
	{
		int valueIndex = index + 1;
		if (valueIndex >= args.Length || LooksLikeOption(args[valueIndex]))
		{
			throw new ArgumentException("--portable-data requires a path value (value suppressed)");
		}

		if (string.IsNullOrWhiteSpace(args[valueIndex]))
		{
			throw new ArgumentException("--portable-data path value is empty (value suppressed)");
		}

		index = valueIndex;
		return args[valueIndex];
	}

	// W8.2. Consumes the SEPARATE value token of --safe-mode-action and advances the
	// loop past it. Rejection follows the --portable-data shape exactly (last token,
	// next token is an option, or blank), and no rejection ever carries the value.
	private static string TakeSafeModeActionValue(string[] args, ref int index)
	{
		int valueIndex = index + 1;
		if (valueIndex >= args.Length || LooksLikeOption(args[valueIndex]))
		{
			throw new ArgumentException("--safe-mode-action requires an action id (value suppressed)");
		}

		if (string.IsNullOrWhiteSpace(args[valueIndex]))
		{
			throw new ArgumentException("--safe-mode-action value is empty (value suppressed)");
		}

		index = valueIndex;
		return args[valueIndex];
	}

	// The parser's own notion of "this token is an option", reused so the value
	// check cannot drift from the unknown-option branch: the "--"/"/" prefixes it
	// uses, plus the single-dash -h synonym that IsHelpOption recognizes.
	// DELIBERATELY NOT "starts with a dash", so "--portable-data -x" keeps "-x" as a
	// data root while a bare "-x" elsewhere is still rejected as an invalid URL: a
	// directory name may legitimately begin with a dash, an http/https URL never does.
	private static bool LooksLikeOption(string arg)
	{
		return arg.StartsWith("--", StringComparison.Ordinal)
			|| arg.StartsWith('/')
			|| IsHelpOption(arg);
	}

	private static bool IsSafeModeOption(string arg)
	{
		return arg.Equals("--safe-mode", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsNoUiOption(string arg)
	{
		return arg.Equals("--no-ui", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsHelpOption(string arg)
	{
		return arg.Equals("--help", StringComparison.OrdinalIgnoreCase)
		|| arg.Equals("-h", StringComparison.OrdinalIgnoreCase)
		|| arg.Equals("/?", StringComparison.Ordinal);
	}

	private static bool IsRegisterOption(string arg)
	{
		return arg.Equals("--register", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsUnregisterOption(string arg)
	{
		return arg.Equals("--unregister", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsVerifyDefaultsOption(string arg)
	{
		return arg.Equals("--verify-defaults", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsOpenDefaultAppsOption(string arg)
	{
		return arg.Equals("--open-default-apps", StringComparison.OrdinalIgnoreCase);
	}

	// 0A-5 (D-v3.5-072). Matched by exact name only: the "--portable-data=<path>"
	// spelling is deliberately NOT this option.
	private static bool IsPortableDataOption(string arg)
	{
		return arg.Equals("--portable-data", StringComparison.OrdinalIgnoreCase);
	}

	// W8.2. The safe-mode selector, matched by exact name only (no "--opt=value" form).
	private static bool IsSafeModeActionOption(string arg)
	{
		return arg.Equals("--safe-mode-action", StringComparison.OrdinalIgnoreCase);
	}

	// W8.1. The Settings-shell entry, matched by exact name only. Public so the App's
	// --settings routing and this parser cannot drift about what the flag is spelled.
	public static bool IsSettingsOption(string arg)
	{
		return arg.Equals("--settings", StringComparison.OrdinalIgnoreCase);
	}
}
