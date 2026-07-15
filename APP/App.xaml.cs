using Microsoft.UI.Xaml;

namespace BootSequence;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
            SystemServices.AppDiagnostics.Logger.Error("ui.unhandled", args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
