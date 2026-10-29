using Avalonia.Controls;

namespace BroChoos.App.Picker;

internal partial class CleanupRecoveryToastWindow : Window
{
	public static TimeSpan Lifetime => TimeSpan.FromSeconds(8);

	public CleanupRecoveryToastWindow()
	{
		InitializeComponent();
	}
}
