namespace BroChoos.Core.Abstractions;

public enum LaunchStatus
{
	Success,
	Failed,
	Timeout,
	Unknown
}

// D-308/D-309: launch outcomes travel as data, never as raw exceptions.
// FailureCategory/ErrorCode explain a Failed outcome; UserVisibleMessageKey is a
// localization key, not a ready-made message.
// There is deliberately no Cancelled state: the 0A-3 Picker's cancel opens nothing and
// returns exit 0, so no cancel ever travels as launch data; a Cancelled state arrives only with a feature that needs it.
public sealed record LaunchResult(
	LaunchStatus Status,
	string TargetId,
	string? ErrorCode,
	string? FailureCategory,
	string? UserVisibleMessageKey)
{
	public static LaunchResult Succeeded(string targetId)
	{
		return new LaunchResult(LaunchStatus.Success, targetId, null, null, null);
	}

	public static LaunchResult Failed(string targetId, string errorCode, string failureCategory, string userVisibleMessageKey)
	{
		return new LaunchResult(LaunchStatus.Failed, targetId, errorCode, failureCategory, userVisibleMessageKey);
	}

	public static LaunchResult Timeout(string targetId, string errorCode, string failureCategory, string userVisibleMessageKey)
	{
		return new LaunchResult(LaunchStatus.Timeout, targetId, errorCode, failureCategory, userVisibleMessageKey);
	}

	public static LaunchResult Unknown(string targetId, string errorCode, string failureCategory, string userVisibleMessageKey)
	{
		return new LaunchResult(LaunchStatus.Unknown, targetId, errorCode, failureCategory, userVisibleMessageKey);
	}
}
