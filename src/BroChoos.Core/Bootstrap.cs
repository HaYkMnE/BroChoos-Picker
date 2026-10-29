using System.Diagnostics;
using BroChoos.Core.Cli;
using BroChoos.Core.Ingress;
using BroChoos.Core.Runners;
using Microsoft.Extensions.Logging;

namespace BroChoos.Core;

public sealed class Bootstrap(
	ILogger<Bootstrap> logger,
	CliParser parser,
	UrlRunner urlRunner,
	SafeModeRunner safeModeRunner,
	DefaultsRunner defaultsRunner)
{
	private readonly ILogger<Bootstrap> _logger = logger;
	private readonly CliParser _parser = parser;
	private readonly UrlRunner _urlRunner = urlRunner;
	private readonly SafeModeRunner _safeModeRunner = safeModeRunner;
	private readonly DefaultsRunner _defaultsRunner = defaultsRunner;

	public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
	{
		try
		{
			ParsedArgs parsed = _parser.Parse(args);

			if (parsed.SafeMode)
			{
				// Safe mode takes precedence over any other input; name the ignored
				// combination, but never echo values back (they may be the user's URL).
				// The invalid selector is named separately from the other ignored input, so
				// the warning only claims "other input is ignored" when there is some.
				string? ignored = DescribeIgnoredInput(parsed);
				if (parsed.SafeModeActionInvalid)
				{
					if (ignored is null)
					{
						_logger.LogWarning("--safe-mode was combined with an invalid --safe-mode-action selector; the invalid selector makes the run fail");
					}
					else
					{
						_logger.LogWarning("--safe-mode was combined with {IgnoredInput} and an invalid --safe-mode-action selector; safe mode takes precedence, the other input is ignored and the invalid selector makes the run fail", ignored);
					}
				}
				else if (ignored is not null)
				{
					_logger.LogWarning("--safe-mode was combined with {IgnoredInput}; safe mode takes precedence, the rest is ignored", ignored);
				}

				_logger.LogInformation("Entering safe mode");
				return await _safeModeRunner.RunAsync(
					parsed.SafeModeActionInvalid
						? SafeModeRequest.InvalidSelector
						: parsed.SafeModeAction is null
							? SafeModeRequest.All
							: SafeModeRequest.Only(parsed.SafeModeAction),
					parsed.PortableDataPath,
					cancellationToken);
			}

			if (parsed.ShowHelp)
			{
				Console.WriteLine(HelpText.Render());

				if (parsed.HasUrl)
				{
					// Help wins over the URL; warn once, never echoing the URL value.
					_logger.LogWarning("A positional URL was provided together with --help; the URL is ignored (value suppressed)");
				}

				if (parsed.HasAnyDefaultsAction)
				{
					// Help also wins over a default-apps action: a command line containing
					// --help never mutates anything.
					_logger.LogWarning("--help was combined with a default-apps option; the default-apps action is ignored");
				}

				return ExitCodes.Success;
			}

			// 0A-4 default-apps actions (after safe mode and help, before the URL
			// path). A combined URL/--no-ui is ignored with a value-free warning:
			// these actions take no URL and open nothing.
			if (parsed.HasAnyDefaultsAction)
			{
				if (parsed.HasUrl || parsed.NoUi)
				{
					_logger.LogWarning("A default-apps option was combined with URL-path input; the rest of the command line is ignored (values suppressed)");
				}

				return parsed.Register
					? _defaultsRunner.Register()
					: parsed.Unregister
					? _defaultsRunner.Unregister()
					: parsed.VerifyDefaults
					? _defaultsRunner.VerifyDefaults()
					: _defaultsRunner.OpenDefaultAppsGuide();
			}

			if (parsed.HasUrl)
			{
				ActivationModifiers modifiers = parsed.NoUi ? ActivationModifiers.NoUi : ActivationModifiers.None;
				ActivationRequest request = new(
					parsed.Url!,
					Stopwatch.GetTimestamp(),
					ActivationTriggerContext.CommandLine,
					SourceEvidence.Confirmed("cli"),
					modifiers,
					Guid.NewGuid(),
					1,
					DateTimeOffset.UtcNow.AddMinutes(5),
					parsed.PortableDataPath);

				Console.WriteLine("[startup] INGRESS_ACCEPTED");

				int urlExitCode = await _urlRunner.RunAsync(request, cancellationToken);
				if (urlExitCode == ExitCodes.ConfigCorruption)
				{
					_logger.LogWarning("URL path reported config corruption; entering safe mode");
					return await _safeModeRunner.RunAsync(SafeModeRequest.ConfigCorruptionFallback, portableDataArgument: null, cancellationToken);
				}

				return urlExitCode;
			}

			Console.WriteLine(HelpText.Render());
			return ExitCodes.NoArgs;
		}
		catch (ArgumentException ex)
		{
			_logger.LogWarning("Invalid arguments: {Message}", ex.Message);
			return ExitCodes.InvalidUrl;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unexpected failure");
			return ExitCodes.GeneralError;
		}
	}

	// Names the ignored combination by counts and option kinds only — never raw values.
	private static string? DescribeIgnoredInput(ParsedArgs parsed)
	{
		List<string> parts = new(3);

		if (parsed.IgnoredPositionalCount == 1)
		{
			parts.Add("a positional argument");
		}
		else if (parsed.IgnoredPositionalCount > 1)
		{
			parts.Add($"{parsed.IgnoredPositionalCount} positional arguments");
		}

		if (parsed.ShowHelp)
		{
			parts.Add("--help");
		}

		if (parsed.Register)
		{
			parts.Add("--register");
		}

		if (parsed.Unregister)
		{
			parts.Add("--unregister");
		}

		if (parsed.VerifyDefaults)
		{
			parts.Add("--verify-defaults");
		}

		if (parsed.OpenDefaultApps)
		{
			parts.Add("--open-default-apps");
		}

		if (parsed.IgnoredUnknownOptionCount == 1)
		{
			parts.Add("an unrecognized option");
		}
		else if (parsed.IgnoredUnknownOptionCount > 1)
		{
			parts.Add($"{parsed.IgnoredUnknownOptionCount} unrecognized options");
		}

		return parts.Count == 0 ? null : string.Join(" and ", parts);
	}
}
