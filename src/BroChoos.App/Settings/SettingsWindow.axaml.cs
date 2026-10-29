using Avalonia.Controls;

namespace BroChoos.App.Settings;

// W8.1 — SettingsWindow. The parameterless constructor is retained so the XAML loader
// and the existing XAML-load probe can construct the window; the store-bearing
// constructor is what the production --settings entry point uses, so the shell reads and
// writes REAL persisted settings rather than an in-memory default.
internal partial class SettingsWindow : Window
{
	public SettingsWindow()
		: this(new SettingsViewModel())
	{
	}

	public SettingsWindow(SettingsViewModel viewModel)
	{
		InitializeComponent();
		DataContext = viewModel;
	}
}
