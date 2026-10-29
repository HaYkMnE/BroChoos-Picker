using System.Diagnostics;
using System.Text.Json;
using BroChoos.Core.Abstractions;

namespace BroChoos.LaunchHelper;

internal static class Program
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	public static int Main(string[] args)
	{
		if (args.Length != 1 || !string.Equals(args[0], "launch", StringComparison.Ordinal))
		{
			return 2;
		}

		string payload = Console.In.ReadToEnd();
		if (string.IsNullOrWhiteSpace(payload))
		{
			WriteOutcome(Fail("unknown", "launch.helper-invalid-request"));
			return 3;
		}

		LaunchHelperRequest? request;
		try
		{
			request = JsonSerializer.Deserialize<LaunchHelperRequest>(payload, JsonOptions);
		}
		catch (JsonException)
		{
			WriteOutcome(Fail("unknown", "launch.helper-invalid-request"));
			return 3;
		}

		if (request is null || !IsValidRequest(request))
		{
			WriteOutcome(Fail(request?.BrowserId ?? "unknown", "launch.helper-invalid-request"));
			return 3;
		}

		LaunchHelperOutcome outcome = Launch(request);
		WriteOutcome(outcome);
		return outcome.Status switch
		{
			LaunchStatus.Success => 0,
			LaunchStatus.Failed => 4,
			LaunchStatus.Timeout => 124,
			LaunchStatus.Unknown => 6,
			_ => 6,
		};
	}

	private static LaunchHelperOutcome Launch(LaunchHelperRequest request)
	{
		if (IsSelf(request.ExePath))
		{
			return Fail(request.BrowserId, "launch.recursion-guard");
		}

		if (!File.Exists(request.ExePath))
		{
			return Fail(request.BrowserId, "launch.target-missing");
		}

		// Profile and private switches are chosen by browser family (LaunchHelperArguments).
		// A private request for a family with no known private switch is REFUSED here, before
		// any process starts, as a typed Failed outcome: never a normal window in its place.
		if (!LaunchHelperArguments.TryBuild(request, out IReadOnlyList<string> arguments, out string? refusal))
		{
			return Fail(request.BrowserId, refusal ?? LaunchHelperArguments.ErrorPrivateUnsupported);
		}

		ProcessStartInfo startInfo = new()
		{
			FileName = request.ExePath,
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(request.ExePath) ?? string.Empty,
		};
		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		try
		{
			using Process? started = Process.Start(startInfo);
			return started is null
				? Fail(request.BrowserId, "launch.start-failed")
				: Success(request.BrowserId);
		}
		catch
		{
			return Fail(request.BrowserId, "launch.start-failed");
		}
	}

	private static bool IsValidRequest(LaunchHelperRequest request)
	{
		return !(string.IsNullOrWhiteSpace(request.BrowserId) || string.IsNullOrWhiteSpace(request.ExePath))
			&& Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri)
			&& uri.Scheme is "http" or "https";
	}

	private static bool IsSelf(string targetPath)
	{
		string? processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath))
		{
			return true;
		}

		try
		{
			return string.Equals(
				Path.GetFullPath(processPath),
				Path.GetFullPath(targetPath),
				StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return true;
		}
	}

	private static LaunchHelperOutcome Success(string targetId)
	{
		return new LaunchHelperOutcome(LaunchStatus.Success, targetId, null, null, null);
	}

	private static LaunchHelperOutcome Fail(string targetId, string errorCode)
	{
		return new LaunchHelperOutcome(LaunchStatus.Failed, targetId, errorCode, "helper-launch", "launch.error.failed");
	}

	private static void WriteOutcome(LaunchHelperOutcome outcome)
	{
		Console.Out.Write(JsonSerializer.Serialize(outcome, JsonOptions));
	}
}
