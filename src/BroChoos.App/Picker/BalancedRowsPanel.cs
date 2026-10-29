using Avalonia;
using Avalonia.Controls;

namespace BroChoos.App.Picker;

// BALANCED, CENTRED TILE ROWS (docs/design/PICKER-LAYOUT-SPEC.md, owner decision).
//
// The tiles fill as few rows as MaxPerRow allows, split EVENLY rather than filled-then-spilled:
//   rows   = ceil(n / MaxPerRow)
//   perRow = ceil(n / rows)
// and the first (n mod rows) rows take one tile more when n does not divide evenly, so 9 -> 5+4,
// 10 -> 5+5, 17 -> 6+6+5, never 8+1. Each row is centred in the width the panel is given, so the
// group's centre is the window's centre even when the window is wider than the tiles (the 360 DIP
// floor, a long status line). Children are taken in order, row by row, left to right, so digit N
// and Left/Right still follow reading order.
//
// All tiles are one size (bc.tile.width/height + margin), so a row is laid out from the first
// child's desired size; that keeps the arithmetic exact and the measure pass cheap.
internal sealed class BalancedRowsPanel : Panel
{
	public static readonly StyledProperty<int> MaxPerRowProperty =
		AvaloniaProperty.Register<BalancedRowsPanel, int>(nameof(MaxPerRow), 8);

	static BalancedRowsPanel()
	{
		AffectsMeasure<BalancedRowsPanel>(MaxPerRowProperty);
	}

	public int MaxPerRow
	{
		get => GetValue(MaxPerRowProperty);
		set => SetValue(MaxPerRowProperty, value);
	}

	// The tile count of each row, top first. Pure arithmetic, so tests can pin it directly.
	internal static IReadOnlyList<int> RowSizes(int count, int maxPerRow)
	{
		if (count <= 0)
		{
			return [];
		}

		int max = Math.Max(1, maxPerRow);
		int rows = (count + max - 1) / max;
		int small = count / rows;
		int extra = count % rows;
		return [.. Enumerable.Range(0, rows).Select(row => row < extra ? small + 1 : small)];
	}

	// A hidden child (IsVisible false) takes no slot: rows are counted and filled from the
	// visible children only, so a hidden tile leaves no gap.
	protected override Size MeasureOverride(Size availableSize)
	{
		Size cell = default;
		int visible = 0;
		foreach (Control child in Children)
		{
			child.Measure(Size.Infinity);
			if (!child.IsVisible)
			{
				continue;
			}

			visible++;
			cell = new Size(Math.Max(cell.Width, child.DesiredSize.Width), Math.Max(cell.Height, child.DesiredSize.Height));
		}

		IReadOnlyList<int> rows = RowSizes(visible, MaxPerRow);
		return rows.Count == 0 ? default : new Size(rows[0] * cell.Width, rows.Count * cell.Height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		List<Control> visible = [.. Children.Where(child => child.IsVisible)];
		Size cell = default;
		foreach (Control child in visible)
		{
			cell = new Size(Math.Max(cell.Width, child.DesiredSize.Width), Math.Max(cell.Height, child.DesiredSize.Height));
		}

		int index = 0;
		double y = 0;
		foreach (int rowSize in RowSizes(visible.Count, MaxPerRow))
		{
			double x = (finalSize.Width - (rowSize * cell.Width)) / 2;
			for (int i = 0; i < rowSize; i++, index++)
			{
				visible[index].Arrange(new Rect(x + (i * cell.Width), y, cell.Width, cell.Height));
			}

			y += cell.Height;
		}

		return finalSize;
	}
}
