// DEBUG-ONLY, wrapping the usings, the namespace and the type declaration - the same second,
// independent gate its sibling AvaloniaProbeRunner carries, beside the configuration-scoped
// Compile Remove in BroChoos.App.csproj. Both files are gated because gating only the entry point
// would leave the probe's Application type compiled into a shipped binary with nothing reaching
// it: dead weight that reads as product code to the next person who finds it.
#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace BroChoos.App.Scaffold0A3;

/// <summary>
/// 0A-3 SCAFFOLD - THROWAWAY. Proves that the Avalonia framework starts inside this
/// solution, under the production TFM, with central package management and the repo's
/// analyzers in force. It carries no Picker behaviour and no product decision, and it
/// must be deleted when the real Picker shell lands.
/// </summary>
internal sealed class AvaloniaProbeApp : Application
{
	// Long enough that the window is genuinely presented and observable, short enough
	// that an unattended run always terminates on its own.
	private static readonly TimeSpan VisibleHold = TimeSpan.FromMilliseconds(1500);

	public override void Initialize()
	{
		// The theme is attached in code on purpose: this scaffold introduces no .axaml
		// file, because .axaml currently has no .gitattributes eol rule and no
		// .editorconfig section in this repo, so it would be an ungoverned file type.
		Styles.Add(new FluentTheme());
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = CreateProbeWindow();
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static Window CreateProbeWindow()
	{
		Window window = new()
		{
			Title = "BroChoos 0A-3 Avalonia probe",
			Width = 460,
			Height = 200,
			WindowStartupLocation = WindowStartupLocation.CenterScreen,
			Content = new TextBlock
			{
				Text = "BroChoos 0A-3 scaffold\nAvalonia started inside BroChoos.sln.\nThis window closes itself.",
				Margin = new Thickness(24),
				TextAlignment = TextAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center,
			},
		};

		window.Opened += OnProbeWindowOpened;
		window.Closed += OnProbeWindowClosed;
		return window;
	}

	private static void OnProbeWindowOpened(object? sender, EventArgs e)
	{
		if (sender is not Window window)
		{
			return;
		}

		// State is read back from the framework and the OS rather than asserted, so the
		// evidence is a measurement and not a hardcoded string.
		nint handle = window.TryGetPlatformHandle()?.Handle ?? 0;
		Console.WriteLine($"[0A-3 probe] WINDOW_OPENED IsVisible={window.IsVisible} hwnd=0x{handle:X}");

		_ = DispatcherTimer.RunOnce(window.Close, VisibleHold);
	}

	private static void OnProbeWindowClosed(object? sender, EventArgs e)
	{
		Console.WriteLine("[0A-3 probe] WINDOW_CLOSED");
	}
}
#endif
