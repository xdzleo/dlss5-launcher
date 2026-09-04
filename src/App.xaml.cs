using System.Diagnostics;
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

        // A chamada FluidScroll.UsarTaxaDoMonitor() saiu daqui.
        //
        // Ela sobrescrevia Timeline.DesiredFrameRate com a taxa do monitor (360 Hz nesta
        // maquina) e a v1.84.0 anunciou isso como ganho. Nao era: medido dentro do app, com a
        // rolagem acontecendo, o tique chega a cada 15,5 ms com o override e a cada 15,9 ms sem
        // ele — os mesmos ~64 Hz. Codigo que nao muda nada medivel, com um log afirmando que
        // muda, e pior do que codigo nenhum.
        //
        // A rolagem agora anda por CompositionTarget.Rendering, que e o ritmo real de composicao
        // da janela — sem prometer numero nenhum.

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

        // Erro de binding do WPF e silencioso: vai para a janela de Output do depurador e para
        // mais ninguem. Um {Binding} apontando para uma propriedade que mudou de nome deixa um
        // pedaco da tela vazio, sem excecao e sem log, e so aparece em relato de usuario. Daqui
        // esses erros vao para o log do launcher, onde a triagem de qualquer problema ja comeca.
        // Refresh() e o que liga o rastreio sem depurador anexado.
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingLogListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

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

/// <summary>Leva os erros de binding do WPF ao log do launcher. Ver <see cref="App.OnStartup"/>.</summary>
internal sealed class BindingLogListener : TraceListener
{
    // O cabecalho (nome da fonte, tipo, id) chega por Write; so a mensagem interessa.
    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) Log.Warn($"binding: {message.Trim()}");
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
                                    int id, string? message)
        => WriteLine(message);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
                                    int id, string? format, params object?[]? args)
    {
        var msg = format;
        if (format is not null && args is { Length: > 0 })
        {
            try { msg = string.Format(format, args); }
            catch (FormatException) { msg = format + " " + string.Join(" ", args); }
        }
        WriteLine(msg);
    }
}
