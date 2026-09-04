using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RenoDXLauncher.Services;

namespace RenoDXLauncher;

/// <summary>
/// Rolagem que anda em vez de saltar, no ritmo do monitor.
///
/// A roda do mouse no WPF nao anima nada: cada clique da roda salta tres linhas de uma vez e
/// para. Num monitor de 60 Hz isso ja e visivel; num de 240 ou 360 e uma teleportacao no meio de
/// uma tela que o resto do tempo esta lisa. O que falta nao e taxa de quadros — e interpolacao:
/// nao existe quadro nenhum ENTRE o antes e o depois para o monitor mostrar.
///
/// Aqui o clique da roda vira um ALVO, e o deslocamento caminha ate ele com desaceleracao. Os
/// cliques se somam: girar tres vezes seguidas nao reinicia o movimento, estica o alvo — que e
/// como a rolagem do sistema se comporta e a razao de ela parecer continua.
/// </summary>
public static class FluidScroll
{
    /// <summary>Quanto cada clique da roda anda. O padrao do WPF (tres linhas) e curto demais
    /// para uma grade de capas, onde o passo util e a altura de uma fileira.</summary>
    private const double PassoDaRoda = 210;

    /// <summary>
    /// Quanto tempo o deslocamento leva ate o alvo.
    ///
    /// Curto de proposito: acima disso a rolagem parece pesada, e a queixa vira "atraso" em vez
    /// de "salto". Com desaceleracao, 190 ms le como movimento continuo e ainda responde no
    /// clique seguinte.
    /// </summary>
    private static readonly Duration Tempo = new(TimeSpan.FromMilliseconds(190));

    public static readonly DependencyProperty AtivoProperty = DependencyProperty.RegisterAttached(
        "Ativo", typeof(bool), typeof(FluidScroll), new PropertyMetadata(false, AoLigar));

    public static void SetAtivo(DependencyObject o, bool v) => o.SetValue(AtivoProperty, v);
    public static bool GetAtivo(DependencyObject o) => (bool)o.GetValue(AtivoProperty);

    /// <summary>O deslocamento animado. Existe porque VerticalOffset e somente-leitura: nao da
    /// para anima-lo direto, entao anima-se este e ele empurra o ScrollViewer a cada quadro.</summary>
    private static readonly DependencyProperty DeslocamentoProperty = DependencyProperty.RegisterAttached(
        "Deslocamento", typeof(double), typeof(FluidScroll),
        new PropertyMetadata(0.0, (o, e) =>
        {
            if (o is not ScrollViewer sv) return;
            var v = (double)e.NewValue;
            sv.ScrollToVerticalOffset(v);
            if (sv.GetValue(EstadoProperty) is Estado st) st.UltimoEmpurrado = v;
        }));

    /// <summary>
    /// Para onde a rolagem esta indo, o que foi empurrado por ultimo, e quando.
    ///
    /// Os tres juntos, porque a pergunta "de onde parte o proximo clique?" so tem resposta com
    /// os tres. Guardar so o alvo tinha dois furos, e os dois davam salto na tela:
    ///
    ///   O alvo ficava velho. Quando um clique substitui a animacao em andamento, o Completed da
    ///   animacao trocada nunca dispara — entao o alvo nunca era limpo, e o clique seguinte,
    ///   dado minutos depois, partia de um numero que nao tinha mais nada a ver com a tela.
    ///
    ///   E a rolagem tem outros donos. Arrastar a barra, a roda do teclado, e o proprio
    ///   ScrollIntoView que a lista faz ao selecionar um jogo mexem no deslocamento sem passar
    ///   por aqui. Comparar o ultimo valor empurrado com o deslocamento atual e o que denuncia
    ///   isso: se alguem mexeu, o alvo antigo nao vale mais.
    /// </summary>
    private sealed class Estado
    {
        public double Alvo = double.NaN;
        public double UltimoEmpurrado = double.NaN;
        public long Quando;
    }

    private static readonly DependencyProperty EstadoProperty = DependencyProperty.RegisterAttached(
        "Estado", typeof(Estado), typeof(FluidScroll), new PropertyMetadata(null));

    private static void AoLigar(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement fe || !(bool)e.NewValue) return;
        if (fe.IsLoaded) Ligar(fe);
        else fe.Loaded += (_, _) => Ligar(fe);
    }

    private static void Ligar(FrameworkElement fe)
    {
        // O ScrollViewer pode ser o proprio elemento (o modal) ou estar dentro do template dele
        // (a ListBox da grade). Procurar depois de carregado e o que cobre os dois casos.
        var sv = fe as ScrollViewer ?? Achar(fe);
        if (sv is null) { Log.Warn("fluid scroll: nenhum ScrollViewer neste elemento"); return; }
        sv.PreviewMouseWheel -= AoGirar;
        sv.PreviewMouseWheel += AoGirar;
    }

    private static ScrollViewer? Achar(DependencyObject raiz)
    {
        if (raiz is ScrollViewer sv) return sv;
        var n = VisualTreeHelper.GetChildrenCount(raiz);
        for (var i = 0; i < n; i++)
            if (Achar(VisualTreeHelper.GetChild(raiz, i)) is { } achado) return achado;
        return null;
    }

    private static void AoGirar(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.Delta == 0) return;
        // Conteudo que cabe na tela nao rola: deixa o evento seguir, para o ScrollViewer de fora
        // (quando ha um) receber a roda em vez de ela morrer aqui.
        if (sv.ScrollableHeight <= 0) return;

        if (sv.GetValue(EstadoProperty) is not Estado st)
        {
            st = new Estado();
            sv.SetValue(EstadoProperty, st);
        }

        // De onde parte este clique. O alvo anterior so vale se o movimento AINDA esta indo para
        // la: se passou tempo demais, ou se alguem mexeu na rolagem por fora (barra arrastada,
        // teclado, a lista rolando sozinha ate o jogo selecionado), ele nao vale nada.
        var agora = Environment.TickCount64;
        var meu = !double.IsNaN(st.UltimoEmpurrado)
                  && Math.Abs(sv.VerticalOffset - st.UltimoEmpurrado) < 1.5;
        var partida = !double.IsNaN(st.Alvo) && agora - st.Quando < 400 && meu
                      ? st.Alvo : sv.VerticalOffset;

        // A distancia segue o TAMANHO do giro, e nao so o sentido. Roda de mouse manda 120 por
        // clique; touchpad de precisao manda dezenas de eventos de 8, 12, 20 — com o sinal
        // apenas, cada cocegas no touchpad andava uma fileira inteira.
        var passo = PassoDaRoda * Math.Clamp(Math.Abs(e.Delta) / 120.0, 0.15, 3.0);
        var alvo = Math.Clamp(partida - Math.Sign(e.Delta) * passo, 0, sv.ScrollableHeight);
        st.Alvo = alvo;
        st.Quando = agora;

        // Ja esta onde quer chegar (fim da lista, por exemplo): nao ha o que animar, e devolver
        // o evento deixa o ScrollViewer de fora reagir em vez de a roda morrer aqui.
        if (Math.Abs(alvo - sv.VerticalOffset) < 0.5) return;

        // Desaceleracao no fim, e nao no comeco: o movimento tem de comecar no instante do
        // clique — comecar devagar e exatamente o que faz uma interface parecer com atraso.
        // HoldEnd, e nao Stop. Com Stop, no fim da animacao a propriedade volta ao valor BASE —
        // que e onde a rolagem estava quando o movimento comecou — e o callback obedece: a tela
        // rolava suave ate o alvo e saltava de volta ao topo. Media: 10,13% aos 188 ms, 0% aos
        // 220 ms.
        var anim = new DoubleAnimation(alvo, Tempo, FillBehavior.HoldEnd)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            // Só o ultimo movimento limpa. Uma animacao trocada no meio do caminho nao dispara
            // este Completed, mas a que a substituiu dispara — e se as duas limpassem, a segunda
            // apagaria o alvo da terceira.
            if (st.Alvo != alvo) return;
            // A ordem evita salto: com a animacao ainda segurando o valor, escrever a base nao
            // muda nada na tela; so depois de solta-la e que a base passa a valer — e ela ja e o
            // alvo.
            sv.SetValue(DeslocamentoProperty, alvo);
            sv.BeginAnimation(DeslocamentoProperty, null);
            st.Alvo = double.NaN;
        };
        sv.SetValue(DeslocamentoProperty, sv.VerticalOffset);
        sv.BeginAnimation(DeslocamentoProperty, anim, HandoffBehavior.SnapshotAndReplace);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- taxa de quadros

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettingsA(string? dispositivo, int modo, ref DEVMODE dm);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    }

    private const int EnumCurrentSettings = -1;

    /// <summary>
    /// Faz as animacoes tiquearem na taxa do MONITOR, e nao nos 60 que o WPF assume.
    ///
    /// O WPF amostra as animacoes a 60 Hz por padrao. Num monitor de 360 Hz isso significa que
    /// cada valor novo e repetido por seis quadros: a tela pode desenhar 360 vezes por segundo e
    /// o movimento continua sendo o de 60. Nao e sensacao — e a taxa em que os valores mudam.
    ///
    /// A taxa vem do monitor e nao de um numero fixo: fixar 240 gastaria bateria em tela de 60 e
    /// deixaria dinheiro na mesa numa de 360. O limite de baixo existe porque monitor que reporta
    /// 0 ou 1 Hz (maquina virtual, sessao remota) nao pode desligar a animacao.
    /// </summary>
    public static void UsarTaxaDoMonitor()
    {
        var hz = 60;
        try
        {
            var dm = new DEVMODE { dmDeviceName = "", dmFormName = "" };
            dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            if (EnumDisplaySettingsA(null, EnumCurrentSettings, ref dm) && dm.dmDisplayFrequency > 1)
                hz = (int)dm.dmDisplayFrequency;
        }
        catch (Exception ex) { Log.Warn($"taxa do monitor: {ex.Message}"); }

        hz = Math.Clamp(hz, 60, 360);
        Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(Timeline), new FrameworkPropertyMetadata(hz));
        Log.Info($"animacoes a {hz} Hz (taxa do monitor)");
    }
}
