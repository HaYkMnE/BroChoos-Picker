using BroChoos.App.Picker;
using FluentAssertions;
using Xunit;

namespace BroChoos.App.Tests;

// D-149 near-cursor placement. The math is pure: cursor point, window DIP size,
// the TARGET monitor's scale and physical work area in, physical position out.
// Spike findings pinned here as tests:
//  D1 - the size conversion must use the TARGET screen's scaling (a mixed-DPI
//       under-/over-clamp would only show on real multi-monitor hardware);
//  D2 - inputs exist before any window is shown, so the position can be applied
//       pre-show (the window code assigns Position before Show; no default flash).
public sealed class CursorPlacementTests
{
	private const double WindowWidthDip = 460;
	private const double WindowHeightDip = 302;

	private static readonly CursorWorkArea PrimaryWorkArea = new(0, 0, 1920, 1040);

	[Fact]
	public void ComputeCentered_CentresInTheWorkArea_AtTheTargetScale()
	{
		CursorPlacementResult result = CursorPlacement.ComputeCentered(792, 160, 1.5, PrimaryWorkArea);

		_ = result.WindowWidthPx.Should().Be(1188);
		_ = result.WindowHeightPx.Should().Be(240);
		_ = result.X.Should().Be((1920 - 1188) / 2);
		_ = result.Y.Should().Be((1040 - 240) / 2);
	}

	[Fact]
	public void ComputeCentered_UsesTheSecondaryMonitorOrigin()
	{
		CursorWorkArea secondary = new(-1920, 0, 1920, 1080);
		CursorPlacementResult result = CursorPlacement.ComputeCentered(360, 200, 1.0, secondary);

		_ = result.X.Should().Be(-1920 + ((1920 - 360) / 2));
		_ = result.Y.Should().Be((1080 - 200) / 2);
	}

	[Fact]
	public void ComputeCentered_KeepsTheTopLeftOnScreen_WhenTheWindowIsLargerThanTheWorkArea()
	{
		CursorWorkArea small = new(100, 50, 1024, 600);
		CursorPlacementResult result = CursorPlacement.ComputeCentered(792, 500, 1.5, small);

		_ = result.X.Should().Be(100);
		_ = result.Y.Should().Be(50);
	}

	[Fact]
	public void Compute_NearCursorWithOffset_WhenEverythingFits()
	{
		CursorPlacementResult result = CursorPlacement.Compute(100, 100, WindowWidthDip, WindowHeightDip, 1.0, PrimaryWorkArea);

		_ = result.X.Should().Be(100 + CursorPlacement.OffsetX);
		_ = result.Y.Should().Be(100 + CursorPlacement.OffsetY);
		_ = result.WindowWidthPx.Should().Be(460);
		_ = result.WindowHeightPx.Should().Be(302);
	}

	[Fact]
	public void Compute_CursorAtRightBottom_ClampsInsideTheWorkArea()
	{
		CursorPlacementResult result = CursorPlacement.Compute(1919, 1039, WindowWidthDip, WindowHeightDip, 1.0, PrimaryWorkArea);

		_ = result.X.Should().Be(1920 - 460);
		_ = result.Y.Should().Be(1040 - 302);
	}

	// D1: at scale 1.5 the same DIP size is 690x453 physical pixels. Using the
	// target screen's scale (1.5) must NOT clamp like scale 1.0 would (1460), and
	// must not over-clamp either: the correct edge is 1920 - 690 = 1230.
	[Fact]
	public void Compute_UsesTheTargetScreensScaling_ForTheWindowSize()
	{
		CursorPlacementResult result = CursorPlacement.Compute(1919, 1000, WindowWidthDip, WindowHeightDip, 1.5, PrimaryWorkArea);

		_ = result.WindowWidthPx.Should().Be(690);
		_ = result.WindowHeightPx.Should().Be(453);
		_ = result.X.Should().Be(1920 - 690);
		_ = result.Y.Should().Be(1040 - 453);
	}

	[Fact]
	public void Compute_WindowWiderThanWorkArea_ClampsToTheWorkAreaOrigin()
	{
		CursorWorkArea small = new(0, 0, 400, 300);

		CursorPlacementResult result = CursorPlacement.Compute(100, 100, WindowWidthDip, 500, 1.0, small);

		_ = result.X.Should().Be(0);
		_ = result.Y.Should().Be(0);
	}

	// A secondary monitor left of the primary has negative X coordinates: the
	// clamp must operate on THAT monitor's work area, not the primary's.
	[Fact]
	public void Compute_SecondaryMonitorLeftOfPrimary_StaysOnThatMonitor()
	{
		CursorWorkArea secondary = new(-1920, 0, 1920, 1080);

		CursorPlacementResult result = CursorPlacement.Compute(-1000, 50, WindowWidthDip, WindowHeightDip, 1.0, secondary);

		_ = result.X.Should().Be(-1000 + CursorPlacement.OffsetX);
		_ = result.Y.Should().Be(50 + CursorPlacement.OffsetY);

		CursorPlacementResult clamped = CursorPlacement.Compute(-5, 1070, WindowWidthDip, WindowHeightDip, 1.0, secondary);

		_ = clamped.X.Should().Be(-1920 + 1920 - 460);
		_ = clamped.Y.Should().Be(1080 - 302);

		// And the results must still lie inside the secondary work area.
		_ = (clamped.X >= secondary.X && clamped.X + clamped.WindowWidthPx <= secondary.X + secondary.Width).Should().BeTrue();
		_ = (clamped.Y >= secondary.Y && clamped.Y + clamped.WindowHeightPx <= secondary.Y + secondary.Height).Should().BeTrue();
	}
}
