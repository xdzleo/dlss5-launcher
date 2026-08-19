using System.Windows;
using RenoDXLauncher.Localization;
using RenoDXLauncher.Services;

namespace RenoDXLauncher;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Idioma antes de qualquer coisa que produza texto — inclusive o modo linha de
        // comando, que tambem e traduzido. Config vazia = idioma do Windows.
        L.SetLanguage(LauncherConfig.Load().Language);

        // headless mode: any argument means "run a command and exit" — no window.
        if (e.Args.Length > 0)
        {
            // OnStartup is async void: without this, WPF sees zero windows at the first await
            // and shuts the app down mid-command (OnLastWindowClose is the default).
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = await Cli.RunAsync(e.Args);
            Shutdown(code);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Warn($"unhandled: {args.Exception}");
            DialogWindow.Show(Current?.MainWindow, "Erro inesperado", args.Exception.Message,
                DialogKind.Danger);
            args.Handled = true;
        };

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
