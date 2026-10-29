namespace BroChoos.App.Picker;

// Near-cursor placement (D-149) hardened against the two named spike findings:
//
// D2 (no default-position flash): this is pure math computed from Win32 monitor
// geometry and the window's FIXED DIP size, so the Position is assigned before
// the window is shown; the window is never created at some default position and
// then moved.
//
// D1 (no scaling mix): all inputs below are PHYSICAL pixels of the TARGET
// monitor - cursor point, work area, and the window size converted with the
// TARGET monitor's own scaling. The spike defect used the current window's
// RenderScaling (the monitor the window happened to sit on) against the target
// screen's physical rect; on mixed-DPI setups that over- or under-clamps.
internal readonly record struct CursorWorkArea(int X, int Y, int Width, int Height);

internal readonly record struct CursorPlacementResult(int X, int Y, int WindowWidthPx, int WindowHeightPx);

internal static class CursorPlacement
{
	// Small right-and-down offset so the window does not appear under the pointer.
	public const int OffsetX = 16;
	public const int OffsetY = 16;

	public static CursorPlacementResult Compute(
		int cursorX,
		int cursorY,
		double windowWidthDip,
		double windowHeightDip,
		double targetScale,
		CursorWorkArea workArea)
	{
		int widthPx = Math.Max(1, (int)Math.Round(windowWidthDip * targetScale));
		int heightPx = Math.Max(1, (int)Math.Round(windowHeightDip * targetScale));

		// When the window exceeds the work area the lower bound wins (top-left clamp).
		int maxX = Math.Max(workArea.X, workArea.X + workArea.Width - widthPx);
		int maxY = Math.Max(workArea.Y, workArea.Y + workArea.Height - heightPx);

		int x = Math.Clamp(cursorX + OffsetX, workArea.X, maxX);
		int y = Math.Clamp(cursorY + OffsetY, workArea.Y, maxY);

		return new CursorPlacementResult(x, y, widthPx, heightPx);
	}

	// The tile picker (docs/design/PICKER-LAYOUT-SPEC.md): centred in the work area of the monitor
	// under the cursor, then clamped by the same rule as above, so a window larger than the work
	// area still keeps its top-left corner on screen. Same physical-pixel inputs as Compute.
	public static CursorPlacementResult ComputeCentered(
		double windowWidthDip,
		double windowHeightDip,
		double targetScale,
		CursorWorkArea workArea)
	{
		int widthPx = Math.Max(1, (int)Math.Round(windowWidthDip * targetScale));
		int heightPx = Math.Max(1, (int)Math.Round(windowHeightDip * targetScale));

		int maxX = Math.Max(workArea.X, workArea.X + workArea.Width - widthPx);
		int maxY = Math.Max(workArea.Y, workArea.Y + workArea.Height - heightPx);

		int x = Math.Clamp(workArea.X + ((workArea.Width - widthPx) / 2), workArea.X, maxX);
		int y = Math.Clamp(workArea.Y + ((workArea.Height - heightPx) / 2), workArea.Y, maxY);

		return new CursorPlacementResult(x, y, widthPx, heightPx);
	}
}
