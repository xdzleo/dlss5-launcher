using System.IO;
using RenoDXLauncher.Localization;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

namespace RenoDXLauncher.ViewModels;

public enum ModBadge { None, Available, NexusOnly, Disabled, Enabled, UpdateAvailable }

/// <summary>One tile in the game grid.</summary>
public class GameItemVm : ObservableObject
{
    public GameInfo Game { get; }
    public CatalogEntry? Mod { get; }

    private string? _coverPath;
    private ModState? _state;
    private string? _chosenExe;

    public GameItemVm(GameInfo game, CatalogEntry? mod)
    {
        Game = game;
        Mod = mod;
    }

    public string Name => Game.Name;
    /// <summary>Pasta de jogo desinstalado com arquivos nossos dentro. Ver SobraScanner.</summary>
    public bool EhSobra => Game.EhSobra;

    /// <summary>O tamanho da sobra em MB, para a pergunta ter peso: "apagar 605 MB" e uma
    /// decisao; "apagar arquivos" nao e.</summary>
    public int SobraMb => (int)(Game.SobraBytes / (1024 * 1024));

    public string StoreLabel => Game.Store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Epic => "Epic",
        GameStore.Gog => "GOG",
        GameStore.Xbox => "Xbox",
        GameStore.Ubisoft => "Ubisoft",
        GameStore.EA => "EA",
        GameStore.BattleNet => "Battle.net",
        GameStore.Rockstar => "Rockstar",
        GameStore.Folder => L.T("Main_Store_Folder"),
        _ => L.T("Main_Store_Manual"),
    };

    public string Key => $"{Game.Store}_{Game.AppId ?? Game.InstallDir}";

    /// <summary>
    /// Ha mod do RenoDX para este jogo.
    ///
    /// Sobra nunca tem: a pasta casa com o catalogo pelo NOME, entao "Baldurs Gate 3" continuava
    /// achando o mod dele mesmo com o jogo desinstalado — e o modal oferecia instalar HDR numa
    /// pasta sem executavel. O cartao de sobra e a unica coisa que faz sentido ali.
    /// </summary>
    public bool HasMod => Mod != null && !EhSobra;

    /// <summary>Quem mantém o mod deste jogo (crédito em destaque no modal).</summary>
    public string MaintainerName => string.IsNullOrWhiteSpace(Mod?.Maintainer)
        ? L.T("Main_Maintainer_Community") : Mod!.Maintainer!;

    public string MaintainerInitial
    {
        get
        {
            var n = MaintainerName.TrimStart('(', '[', ' ');
            return n.Length > 0 ? char.ToUpperInvariant(n[0]).ToString() : "?";
        }
    }
    public bool HasDirectDownload => Mod?.DownloadUrl != null;

    /// <summary>Selo de status da wiki: estavel (check verde) x em construcao (aviso).</summary>
    public bool ModIsStable => Mod?.Working == true;
    public bool ModIsUnstable => Mod != null && !Mod.Working;
    public string ModStatusText => L.T(ModIsStable ? "Main_ModStatus_Stable" : "Main_ModStatus_Unstable");

    /// <summary>Reavalia os textos que este item traduz por conta propria. Chamado na troca de
    /// idioma — essas propriedades nao passam por {loc:Tr}, entao nada mais as reavaliaria.</summary>
    public void RaiseLocalizedText()
    {
        OnPropertyChanged(nameof(StoreLabel));
        OnPropertyChanged(nameof(ModStatusText));
        // Tambem traduzem por conta propria: o tooltip do selo de estabilidade e o credito de
        // "comunidade" quando o mod nao tem mantenedor nomeado (a inicial acompanha o nome).
        OnPropertyChanged(nameof(ModStatusTooltip));
        OnPropertyChanged(nameof(MaintainerName));
        OnPropertyChanged(nameof(MaintainerInitial));
    }
    public string ModStatusTooltip => L.T(ModIsStable
        ? "Main_ModStatus_Stable_Tooltip"
        : "Main_ModStatus_Unstable_Tooltip");

    public string? CoverPath { get => _coverPath; set { if (Set(ref _coverPath, value)) OnPropertyChanged(nameof(HasCover)); } }
    public bool HasCover => _coverPath != null;
    public string Initials
    {
        get
        {
            var words = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));
        }
    }

    /// <summary>Exe the mod targets (pinned by user or best heuristic candidate).</summary>
    public string? ChosenExe
    {
        get => _chosenExe;
        set
        {
            if (Set(ref _chosenExe, value))
            {
                OnPropertyChanged(nameof(TargetDir));
                RefreshState();
            }
        }
    }

    public string? TargetDir => _chosenExe != null ? Path.GetDirectoryName(_chosenExe) : null;

    public ModState? State
    {
        get => _state;
        private set
        {
            _state = value;
            // O estado do DLSS 5 e relido AQUI, uma vez por mudanca de estado, e nao a cada
            // leitura da propriedade: a lista pinta os cards muitas vezes, e um File.Exists +
            // parse de ini por repintura deixava a selecao de jogo visivelmente lenta.
            _dlss5Ligado = LerDlss5Ligado();
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(Badge));
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(IsEnabled));
            // Sem estas duas as bolinhas so mudavam de cor quando a lista era reconstruida:
            // instalar deixava o interruptor verde e o card do jogo vermelho ao mesmo tempo.
            OnPropertyChanged(nameof(LuzDlss5));
            OnPropertyChanged(nameof(LuzHdr));
        }
    }

    // A sobra nao tem mod instalado — tem RESTO de mod.
    //
    // O jogo foi desinstalado e os arquivos ficaram; ler "addon presente" ali e dizer que ha uma
    // instalacao viva numa pasta sem executavel. O sintoma era o cartao pedindo atualizacao de um
    // mod para um jogo que nao existe mais, todo dia, sem jeito de calar.
    public bool IsInstalled => !EhSobra && _state?.AddonPath != null;
    public bool IsEnabled => !EhSobra && _state?.AddonEnabled == true;

    private bool _hasUpdate;
    /// <summary>Uma build mais nova deste mod está disponível no servidor.</summary>
    public bool HasUpdate
    {
        get => _hasUpdate;
        set
        {
            if (Set(ref _hasUpdate, value))
            {
                OnPropertyChanged(nameof(Badge));
            }
        }
    }

    public ModBadge Badge =>
        EhSobra ? ModBadge.None :
        _hasUpdate && _state?.AddonPath != null ? ModBadge.UpdateAvailable :
        _state?.AddonPath != null
            ? (_state.AddonEnabled ? ModBadge.Enabled : ModBadge.Disabled)
            : Mod is null ? ModBadge.None
            : Mod.DownloadUrl != null ? ModBadge.Available
            : ModBadge.NexusOnly;

    // BadgeText saiu junto com o selo laranja que ele alimentava. Badge continua, porque e ele
    // que decide a bolinha do RenoDX HDR no cartao da grade.

    // ----- os dois recursos, lado a lado -----
    //
    // Um selo unico dizia "ENABLED" e nao dizia ENABLED O QUE: o launcher instala duas coisas
    // independentes, e um jogo pode ter DLSS 5 rodando com o mod HDR desligado, ou o contrario.
    // Duas bolinhas nomeadas dizem o estado dos dois de uma olhada, sem abrir o jogo.
    //
    // Cinza nao e "desligado": e "nao existe para este jogo". A diferenca importa — vermelho
    // convida a clicar, cinza avisa que nao ha o que clicar.

    /// <summary>Verde = ligado · vermelho = presente e desligado · cinza = indisponível.</summary>
    public enum Luz { Cinza, Vermelha, Verde }

    /// <summary>DLSS 5 neste jogo. Nunca fica cinza: o Feeder atende jogo sem DLSS nenhum,
    /// então a opção existe em qualquer título que o launcher liste.</summary>
    public Luz LuzDlss5 => EhSobra ? Luz.Cinza : Dlss5Ligado ? Luz.Verde : Luz.Vermelha;

    /// <summary>
    /// O mod HDR do RenoDX. Cinza quando não há mod para este jogo — e é a maioria: o catálogo
    /// cobre uma lista específica de títulos, e prometer um interruptor que não existe é pior do
    /// que dizer que não existe.
    /// </summary>
    // Na sobra as duas apagam. Os arquivos ate estao la — foi o launcher que os pos — mas dizer
    // "DLSS 5 ligado" numa pasta sem jogo e afirmar que algo funciona quando nao ha o que rodar.
    public Luz LuzHdr =>
        EhSobra ? Luz.Cinza
        : Mod is null && _state?.AddonPath is null ? Luz.Cinza
        : _state?.AddonEnabled == true ? Luz.Verde
        : Luz.Vermelha;

    /// <summary>
    /// O interruptor de DLSS 5 está ligado nesta pasta.
    ///
    /// Guardado, não recalculado: quem lê é o binding das bolinhas, e a lista repinta os cards
    /// muitas vezes — fazer I/O de disco a cada repintura deixava a seleção de jogo lenta de um
    /// jeito perceptível. O valor é atualizado quando o estado muda, que é quando ele pode mudar.
    /// </summary>
    private bool _dlss5Ligado;
    public bool Dlss5Ligado => _dlss5Ligado;

    /// <summary>
    /// Lê a chave que o addon consulta, nos dois contratos: o novo (`[RENODX-DLSS]`) e o antigo
    /// (`[RenoDX.DLSS5]`). A cadeia completa só é medida na tela de detalhe, que é onde há tempo
    /// de tocar o disco; aqui basta o interruptor.
    /// </summary>
    private bool LerDlss5Ligado()
    {
        // O exe escolhido diz a pasta, mas ele pode faltar: a deteccao acha o addon e depois
        // procura um .exe ao lado, e nem toda pasta de deploy tem um. Nesse caso o proprio addon
        // encontrado diz onde a instalacao esta — desistir ali apagava a bolinha de um jogo
        // instalado e funcionando.
        var dir = TargetDir
                  ?? (_state?.AddonPath is { } a ? Path.GetDirectoryName(a) : null);
        if (dir is null) return false;
        try
        {
            var ini = _state?.IniPath ?? Path.Combine(dir, "ReShade.ini");
            return File.Exists(ini) && NeuralUpliftService.IsApplied(dir, ini, _state?.AddonPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// Relê o estado das duas luzes e avisa a interface.
    ///
    /// Chamado depois de instalar ou remover: sem isto, o interruptor da tela de detalhe virava
    /// verde e a bolinha do card continuava vermelha, porque nada tinha mexido em State.
    /// </summary>
    public void RefreshLuzes()
    {
        _dlss5Ligado = LerDlss5Ligado();
        OnPropertyChanged(nameof(Dlss5Ligado));
        OnPropertyChanged(nameof(LuzDlss5));
        OnPropertyChanged(nameof(LuzHdr));
    }

    public void RefreshState()
    {
        if (TargetDir is null) { State = null; return; }
        State = AddonService.GetState(TargetDir, _chosenExe);
    }

    /// <summary>Apply pre-computed detection results (exe + state) from a background thread.
    /// Must be called on the UI thread; does no disk I/O.</summary>
    public void ApplyDetected(string? exe, ModState? state)
    {
        if (exe != null)
        {
            _chosenExe = exe;
            OnPropertyChanged(nameof(ChosenExe));
            OnPropertyChanged(nameof(TargetDir));
        }
        if (state != null) State = state;

        // As luzes sao lidas AQUI tambem, e nao so no setter de State.
        //
        // `state` e null em jogo sem mod do RenoDX -- que e a maioria da lista -- e nesse caso o
        // setter nunca rodava. O resultado era a bolinha de DLSS 5 nascer vermelha em todo jogo,
        // inclusive nos que estavam com o DLSS 5 instalado, e so acertar quando o usuario
        // clicasse no jogo (o que dispara RefreshLuzes por outro caminho).
        //
        // O estado do DLSS 5 nao depende de haver mod HDR: sao duas coisas independentes, que e
        // exatamente o motivo de existirem duas bolinhas.
        RefreshLuzes();
    }

    /// <summary>Pastas que o proprio launcher cria e que nunca sao o alvo de uma instalacao.</summary>
    private static bool EhAndaime(string arquivo)
    {
        var pasta = Path.GetFileName(Path.GetDirectoryName(arquivo)) ?? "";
        return pasta.Equals(FeederService.Host64Dir, StringComparison.OrdinalIgnoreCase)
               || pasta.Equals("_mods_desligados", StringComparison.OrdinalIgnoreCase)
               || pasta.Equals("vklayer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Find an existing RenoDX install anywhere in the game dir (installed manually or by
    /// a previous run) and the exe that lives beside it. Pure disk I/O — safe on any thread;
    /// feed the result to ApplyDetected on the UI thread.</summary>
    public (string? exe, ModState? state) DetectExistingInstall()
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 5,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            // A pasta do jogo ANTES da varredura recursiva, e por dois motivos.
            //
            // Barato: a instalacao esta na raiz na grande maioria dos casos, e resolver ali evita
            // descer a arvore inteira — o que passou a importar quando isto deixou de rodar so em
            // jogo com mod e passou a rodar em todos.
            //
            // E correto: `host64` e andaime NOSSO, a metade de 64 bits que o Feeder usa em jogo de
            // 32 bits. O addon esta la tambem, e uma varredura recursiva podia encontrar aquele
            // primeiro e fixar como alvo uma pasta cujo unico executavel e o host — nao o jogo.
            var addon = Directory.EnumerateFiles(Game.InstallDir, "renodx-*.addon*",
                                                 SearchOption.TopDirectoryOnly).FirstOrDefault()
                        ?? Directory.EnumerateFiles(Game.InstallDir, "renodx-*.addon*", options)
                            .FirstOrDefault(f => !EhAndaime(f));
            if (addon is null) return (null, null);
            // the addon's own folder is the deploy dir — pin the exe there so selection,
            // toggle and settings all operate on the real install location
            var dir = Path.GetDirectoryName(addon)!;
            var exe = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                .FirstOrDefault();
            return (exe, AddonService.GetState(dir, exe));
        }
        catch (Exception ex)
        {
            Log.Warn($"detect existing {Name}: {ex.Message}");
            return (null, null);
        }
    }
}
