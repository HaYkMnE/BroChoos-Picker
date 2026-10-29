using Avalonia.Controls;

namespace BroChoos.App.Picker;

internal partial class GhostToastWindow : Window
{
	public static TimeSpan Lifetime => TimeSpan.FromSeconds(3);

	public static TimeSpan RateLimit => TimeSpan.FromSeconds(30);

	public GhostToastWindow()
	{
		InitializeComponent();
	}
}
