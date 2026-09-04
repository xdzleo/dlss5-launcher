using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        /// <summary>Ha uma animacao NOSSA correndo agora. E o unico caso em que o alvo vale.</summary>
        public bool Correndo;
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
        // A barra, e nao o ScrollChanged.
        //
        // A v1.85.0 escutava ScrollChanged e comparava o deslocamento novo com o ultimo valor
        // empurrado, para descobrir se a mudanca tinha sido nossa. Duas coisas erradas nisso, e
        // as duas aparecem justamente quando se rola rapido:
        //
        //   A animacao tiquea a 360 Hz e o ScrollChanged chega por passagem de layout — varios
        //   empurroes cabem entre dois eventos. O evento entao trazia um deslocamento ATRASADO
        //   em relacao ao ultimo empurrao, a comparacao dava "nao foi a gente", e a animacao se
        //   cancelava sozinha no meio do movimento.
        //
        //   E o cancelamento acontecia DENTRO do callback da propriedade que estava sendo
        //   animada — mexer na animacao ali e reentrancia, e o resultado nao e previsivel.
        //
        // O evento Scroll da barra nao tem nenhum dos dois problemas: ele dispara so quando a
        // PESSOA mexe na barra (arrasta o cursor, clica na pista), nunca por mudanca
        // programatica. E o unico caso que precisava mesmo ser interrompido.
        sv.RemoveHandler(ScrollBar.ScrollEvent,
                         (ScrollEventHandler)AoArrastarBarra);
        sv.AddHandler(ScrollBar.ScrollEvent,
                      (ScrollEventHandler)AoArrastarBarra);
    }

    /// <summary>A pessoa pegou a barra de rolagem: solta a animacao onde ela esta, para as duas
    /// nao puxarem o mesmo deslocamento para lados diferentes.</summary>
    private static void AoArrastarBarra(object sender, ScrollEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (sv.GetValue(EstadoProperty) is not Estado st || !st.Correndo) return;
        Soltar(sv, st, sv.VerticalOffset);
    }

    /// <summary>Tira a animacao do caminho deixando o deslocamento onde esta.</summary>
    private static void Soltar(ScrollViewer sv, Estado st, double onde)
    {
        sv.SetValue(DeslocamentoProperty, onde);
        sv.BeginAnimation(DeslocamentoProperty, null);
        st.Correndo = false;
        st.Alvo = double.NaN;
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

        // De onde parte este clique: do alvo, quando ha uma animacao NOSSA a caminho dele; do
        // deslocamento real, em qualquer outro caso.
        //
        // Antes isto era decidido comparando o ultimo valor empurrado com o deslocamento atual, e
        // essa comparacao tinha uma corrida: ScrollToVerticalOffset nao muda VerticalOffset na
        // hora — o valor so aparece depois do proximo arranjo. Uma roda girada no intervalo lia
        // os dois diferentes, concluia "alguem mexeu por fora" e recomecava do zero. O sintoma e
        // justamente o de girar rapido: parte do caminho some.
        //
        // O relogio continua no criterio para o caso de a animacao ter sido interrompida sem
        // avisar (elemento removido da arvore no meio do movimento).
        var agora = Environment.TickCount64;
        // Somar cliques so vale enquanto eles vao para o MESMO lado.
        //
        // Este era o defeito que aparecia ao descer rapido e subir logo depois. Cinco cliques
        // para baixo deixam o alvo muito abaixo do que a tela mostra — o movimento ainda esta a
        // caminho dele. O clique para cima partia desse alvo e tirava uma fileira dele, ou seja:
        // continuava sendo um destino la embaixo. A tela seguia DESCENDO depois de a pessoa ter
        // mandado subir, e so muitos cliques depois e que o sentido virava.
        //
        // Invertendo a direcao, a conta parte de onde o olho esta — o deslocamento atual — e o
        // movimento troca de sentido no primeiro clique, que e o que qualquer rolagem do sistema
        // faz.
        var descendo = e.Delta < 0;
        var mesmoLado = !double.IsNaN(st.Alvo) && (st.Alvo > sv.VerticalOffset) == descendo;
        var partida = st.Correndo && mesmoLado && agora - st.Quando < 1000
                      ? st.Alvo : sv.VerticalOffset;

        // A distancia segue o TAMANHO do giro, e nao so o sentido. Roda de mouse manda 120 por
        // clique; touchpad de precisao manda dezenas de eventos de 8, 12, 20 — com o sinal
        // apenas, cada cocegas no touchpad andava uma fileira inteira.
        //
        // E nunca mais que meia tela: 210 px sao uma fileira na grade e quase um cartao inteiro
        // dentro do modal, que e bem menor. O passo pertence a area que rola, nao ao mouse.
        var passo = PassoDaRoda * Math.Clamp(Math.Abs(e.Delta) / 120.0, 0.15, 3.0);
        passo = Math.Min(passo, Math.Max(sv.ViewportHeight * 0.5, 60));
        var alvo = Math.Clamp(partida - Math.Sign(e.Delta) * passo, 0, sv.ScrollableHeight);

        // Ja esta onde quer chegar (fim da lista, por exemplo): sai ANTES de mexer no estado.
        // Mexer e sair deixava o alvo apontando para um lugar para onde nenhuma animacao ia — e
        // a animacao em curso, ao terminar, via o alvo trocado e nao fazia a limpeza dela.
        if (Math.Abs(alvo - sv.VerticalOffset) < 0.5) return;

        st.Alvo = alvo;
        st.Quando = agora;
        st.Correndo = true;

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
            st.Correndo = false;
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
