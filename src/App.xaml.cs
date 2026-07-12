using System.Windows;
using RenoDXLauncher.Services;

namespace RenoDXLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Warn($"unhandled: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "RenoDX Launcher — erro inesperado",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
