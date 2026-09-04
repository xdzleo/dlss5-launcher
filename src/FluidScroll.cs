using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RenoDXLauncher.Services;

namespace RenoDXLauncher;

/// <summary>
/// Rolagem com inercia: o giro da roda vira VELOCIDADE, e a velocidade decai sozinha.
///
/// A roda do WPF nao anima nada — cada clique salta tres linhas e para. A primeira tentativa de
/// consertar isso animava um tween de 190 ms por clique. Ficou melhor, e nao ficou liso: quem
/// usa notou que "pela barra fica liso, pela roda nao".
///
/// A diferenca entre os dois nao era taxa de quadros. Foi medido dentro do app, com a rolagem
/// acontecendo de verdade: o tique chega a cada 15,5 ms (~64 Hz) nos dois casos, e o
/// deslocamento aceita fracao de pixel (0 de 12 passos caiam em pixel inteiro). O que separava
/// os dois e que arrastar a barra e UM movimento continuo, enquanto um tween por clique e uma
/// sequencia de arranques: cada clique comecava uma curva nova em velocidade cheia por cima de
/// outra que estava desacelerando. Num giro seguido isso e um trem de solavancos no ritmo dos
/// cliques.
///
/// Aqui o clique nao diz para onde ir: ele EMPURRA. A velocidade soma, decai por exponencial, e
/// a posicao e a integral dela. Nao ha curva para reiniciar, entao girar depressa acelera de
/// verdade, e inverter o sentido e somar um impulso negativo — a mesma conta, sem caso especial.
/// E o que a rolagem por inercia do Windows e do macOS faz.
/// </summary>
public static class FluidScroll
{
    /// <summary>
    /// Quanto um clique da roda empurra, em pixels por segundo.
    ///
    /// Com o decaimento abaixo, um impulso sozinho percorre <c>Impulso * Tau</c> — 2000 x 0,11 =
    /// 220 px, que e aproximadamente a altura de uma fileira de capas. Dois cliques seguidos
    /// percorrem o dobro, porque somam antes de decair: e dai que vem a sensacao de embalo.
    /// </summary>
    private const double Impulso = 2000;

    /// <summary>
    /// A constante de tempo do decaimento: a velocidade cai a 37% dela a cada Tau.
    ///
    /// 110 ms e curto o bastante para o movimento parecer preso ao dedo e longo o bastante para
    /// a parada ser um assentamento, e nao um corte. Abaixo de ~70 ms le como tranco; acima de
    /// ~200 ms a lista parece patinar depois que a pessoa parou de girar.
    /// </summary>
    private const double Tau = 0.11;

    /// <summary>
    /// Onde o movimento acaba. Nao e zero, de proposito.
    ///
    /// Com decaimento exponencial a velocidade nunca chega a zero, so encolhe. Abaixo deste
    /// limite o que resta de caminho e <c>12 x 0,11 = 1,3 px</c>: seguir adiante seria arrastar
    /// um pixel por varios quadros — o rastejo que se nota justamente quando o olho ja assentou.
    /// </summary>
    private const double VelocidadeMinima = 12;

    /// <summary>Teto do embalo, para uma roda girada com raiva nao teleportar a lista.</summary>
    private const double VelocidadeMaxima = 9000;

    /// <summary>
    /// Um quadro perdido nao teleporta a tela.
    ///
    /// A posicao e integrada por dt, entao uma pausa da thread — coleta de lixo, uma capa
    /// decodificada, a janela voltando do minimizado — viraria um salto do tamanho da pausa.
    /// Limitar o dt troca o salto por um atraso, que e o erro menos visivel dos dois.
    /// </summary>
    private const double PassoMaximo = 0.025;

    private static readonly Stopwatch Relogio = Stopwatch.StartNew();

    public static readonly DependencyProperty AtivoProperty = DependencyProperty.RegisterAttached(
        "Ativo", typeof(bool), typeof(FluidScroll), new PropertyMetadata(false, AoLigar));

    public static void SetAtivo(DependencyObject o, bool v) => o.SetValue(AtivoProperty, v);
    public static bool GetAtivo(DependencyObject o) => (bool)o.GetValue(AtivoProperty);

    /// <summary>O movimento de UM ScrollViewer: para onde ele vai e a que velocidade.</summary>
    private sealed class Estado
    {
        public double Velocidade;

        /// <summary>
        /// A posicao em ponto flutuante.
        ///
        /// Separada de VerticalOffset de proposito: integrar em cima do valor que o ScrollViewer
        /// devolve perderia a fracao a cada quadro, e o movimento andaria em degraus de um pixel.
        /// </summary>
        public double Posicao;

        public bool Andando;
        public long UltimoTique;
    }

    private static readonly DependencyProperty EstadoProperty = DependencyProperty.RegisterAttached(
        "Estado", typeof(Estado), typeof(FluidScroll), new PropertyMetadata(null));

    /// <summary>
    /// O relogio do movimento: uma animacao que nao anima nada, so tiquea.
    ///
    /// A primeira versao integrava a velocidade em CompositionTarget.Rendering, e a medicao dentro
    /// do app mostrou que ele nao serve de relogio aqui: os intervalos crus alternam entre ~0 ms e
    /// ~102 ms, ou seja o evento e disparado em rajadas por quadro composto, e nao num ritmo. Com
    /// dt de 100 ms, um giro anda 118 px de uma vez — pior que o salto que a rolagem veio
    /// consertar.
    ///
    /// O relogio de ANIMACAO do WPF, esse sim, foi medido em 15,5 ms constantes. Entao o pulso
    /// abaixo existe so para receber esses tiques: o valor dele nao importa e nao e lido em lugar
    /// nenhum — o que importa e a chamada.
    /// </summary>
    private static readonly DependencyProperty PulsoProperty = DependencyProperty.RegisterAttached(
        "Pulso", typeof(double), typeof(FluidScroll),
        new PropertyMetadata(0.0, (o, _) =>
        {
            if (o is ScrollViewer sv && sv.GetValue(EstadoProperty) is Estado st && st.Andando)
                Quadro(sv, st);
        }));

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

        // A barra tem prioridade sobre a inercia: quem pega a barra quer aquele lugar, e nao o
        // resto do embalo. O evento Scroll dispara so por acao da pessoa — ao contrario do
        // ScrollChanged, que dispara tambem a cada empurrao nosso e, por chegar por passagem de
        // layout, trazia um valor atrasado que fazia a rolagem se cancelar sozinha.
        sv.RemoveHandler(ScrollBar.ScrollEvent, (ScrollEventHandler)AoArrastarBarra);
        sv.AddHandler(ScrollBar.ScrollEvent, (ScrollEventHandler)AoArrastarBarra);

        // Sumir da tela para o movimento. O modal e escondido por Visibility.Collapsed e NUNCA
        // sai da arvore, entao Unloaded nao dispara nele: um gancho de quadro preso ali deixaria
        // o app desenhando todo quadro para sempre com o modal fechado.
        sv.IsVisibleChanged -= AoMudarVisibilidade;
        sv.IsVisibleChanged += AoMudarVisibilidade;
    }

    private static ScrollViewer? Achar(DependencyObject raiz)
    {
        if (raiz is ScrollViewer sv) return sv;
        var n = VisualTreeHelper.GetChildrenCount(raiz);
        for (var i = 0; i < n; i++)
            if (Achar(VisualTreeHelper.GetChild(raiz, i)) is { } achado) return achado;
        return null;
    }

    private static Estado EstadoDe(ScrollViewer sv)
    {
        if (sv.GetValue(EstadoProperty) is Estado st) return st;
        st = new Estado();
        sv.SetValue(EstadoProperty, st);
        return st;
    }

    private static void AoGirar(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.Delta == 0) return;
        // Conteudo que cabe na tela nao rola: deixa o evento seguir, para o ScrollViewer de fora
        // (quando ha um) receber a roda em vez de ela morrer aqui.
        if (sv.ScrollableHeight <= 0) return;

        var st = EstadoDe(sv);

        // O tamanho do giro conta, e nao so o sentido: roda de mouse manda 120 por clique;
        // touchpad de precisao manda dezenas de eventos de 8, 12, 20.
        var forca = Impulso * Math.Clamp(Math.Abs(e.Delta) / 120.0, 0.15, 3.0);
        // Delta positivo e girar para CIMA, e subir e diminuir o deslocamento — dai o sinal.
        var empurrao = -Math.Sign(e.Delta) * forca;

        // Inverter o sentido MATA o embalo em vez de descontar dele.
        //
        // Somar um impulso contrario e o que a fisica faria, e e errado aqui. Cinco cliques para
        // baixo deixam a velocidade em 9000 px/s; um clique para cima tira 2000 e sobram 7000
        // ainda descendo — a tela continua descendo depois de a pessoa ter mandado subir. Foi
        // exatamente essa a queixa. Rolagem de sistema nenhuma se comporta assim: girar para o
        // outro lado e um cancelamento, e o giro que cancela nao anda quase nada, que e o que se
        // espera de quem esta corrigindo o proprio gesto.
        if (st.Velocidade != 0 && Math.Sign(empurrao) != Math.Sign(st.Velocidade))
            st.Velocidade = 0;

        st.Velocidade = Math.Clamp(st.Velocidade + empurrao, -VelocidadeMaxima, VelocidadeMaxima);

        if (!st.Andando)
        {
            // Parte de onde a tela esta AGORA, que pode ter sido mexida pela barra, pelo teclado
            // ou pela propria lista rolando ate o jogo selecionado.
            st.Posicao = sv.VerticalOffset;
            st.UltimoTique = Relogio.ElapsedTicks;
            st.Andando = true;
            // Duracao longa e repeticao infinita: o pulso so para quando a velocidade acaba.
            sv.BeginAnimation(PulsoProperty, new System.Windows.Media.Animation.DoubleAnimation(
                0, 1, new Duration(TimeSpan.FromSeconds(10)))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
            });
        }
        e.Handled = true;
    }

    private static void Quadro(ScrollViewer sv, Estado st)
    {
        var agora = Relogio.ElapsedTicks;
        var dt = (agora - st.UltimoTique) / (double)Stopwatch.Frequency;
        st.UltimoTique = agora;
        if (dt <= 0) return;
        dt = Math.Min(dt, PassoMaximo);

        st.Posicao += st.Velocidade * dt;
        // Exponencial de verdade, e nao um fator fixo por quadro: o ritmo dos quadros varia, e um
        // fator por quadro faria a rolagem frear mais depressa com a maquina ocupada — justamente
        // quando ela ja parece pior.
        st.Velocidade *= Math.Exp(-dt / Tau);

        // Nas pontas o embalo morre. Sem isto, a velocidade guardada seguiria empurrando contra o
        // fim da lista, e o primeiro giro no sentido contrario seria engolido por ela.
        var limite = sv.ScrollableHeight;
        if (st.Posicao <= 0) { st.Posicao = 0; st.Velocidade = 0; }
        else if (st.Posicao >= limite) { st.Posicao = limite; st.Velocidade = 0; }

        sv.ScrollToVerticalOffset(st.Posicao);
        if (Math.Abs(st.Velocidade) < VelocidadeMinima) Parar(sv, st);
    }

    private static void Parar(ScrollViewer sv, Estado st)
    {
        if (!st.Andando) return;
        st.Andando = false;
        st.Velocidade = 0;
        sv.BeginAnimation(PulsoProperty, null);
    }

    private static void AoArrastarBarra(object sender, ScrollEventArgs e)
    {
        if (sender is ScrollViewer sv && sv.GetValue(EstadoProperty) is Estado st) Parar(sv, st);
    }

    private static void AoMudarVisibilidade(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue && sender is ScrollViewer sv
            && sv.GetValue(EstadoProperty) is Estado st) Parar(sv, st);
    }
}
