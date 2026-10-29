using System.Runtime.InteropServices;

namespace BroChoos.App.Picker;


// Whether the user is in a state where a window must not jump to the front.
// Source: SHQueryUserNotificationState / QUERY_USER_NOTIFICATION_STATE (shellapi.h),
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state
//
// ONLY THE TWO UNAMBIGUOUS STATES COUNT:
//   3 QUNS_RUNNING_D3D_FULL_SCREEN - an exclusive-mode Direct3D app (a game) is running;
//   4 QUNS_PRESENTATION_MODE       - the user turned on Windows presentation settings.
// 2 (QUNS_BUSY) is deliberately NOT treated as busy. The docs define it as "a full-screen
// application is running OR Presentation Settings are applied", which also covers ordinary
// borderless-maximized windows (a fullscreen browser, a video player), so honouring it would
// hold links in everyday use. Missing a fullscreen game that only reports 2 is the cheaper
// error: the picker then behaves exactly as it did before this policy (comes forward).
// One diagnostic call on the dev machine returned hr=0, state=5 (accepts notifications).
//
// FAIL OPEN: if the query fails, the user is treated as NOT busy, so the picker keeps its
// existing behaviour (come to the front) rather than silently never appearing. The caller also
// caps any hold (PickerSession.FullscreenMaxHold).
internal static class UserNotificationState
{
	private const int QunsRunningD3dFullScreen = 3;
	private const int QunsPresentationMode = 4;

	internal static bool IsUserBusy()
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		try
		{
			return SHQueryUserNotificationState(out int state) == 0 && IsBusyState(state);
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	internal static bool IsBusyState(int state)
	{
		return state is QunsRunningD3dFullScreen or QunsPresentationMode;
	}

	[DllImport("shell32.dll")]
	private static extern int SHQueryUserNotificationState(out int state);
}
