using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RenoDXLauncher.Services;

/// <summary>Launcher config: the user's display HDR profile + per-game pinned exe choices.</summary>
public class LauncherConfig
{
    /// <summary>Display peak brightness in nits (Windows HDR Calibration clipping point).</summary>
    public double PeakNits { get; set; } = 1000;
    /// <summary>Paper white / 100% white (ITU BT.2408 reference = 203).</summary>
    public double GameNits { get; set; } = 203;
    public double UiNits { get; set; } = 203;
    /// <summary>Write the display profile into ReShade.ini right after installing a mod.</summary>
    public bool ApplyProfileOnInstall { get; set; } = true;
    /// <summary>game key (store_appid or install dir) → chosen exe path.</summary>
    public Dictionary<string, string> PinnedExes { get; set; } = new();

    /// <summary>
    /// Qual tradutor de Direct3D 9 usar, por jogo: "dxvk" ou "dgvoodoo". Ausente = automatico.
    ///
    /// A escolha e do usuario porque nao ha resposta certa. Os dois tradutores cobrem conjuntos
    /// diferentes de jogos, e os conjuntos nao se contem — medido nesta maquina, com o mesmo
    /// add-on e o mesmo runtime: o Resident Evil Revelations 2 so roda com DXVK (o dgVoodoo
    /// crasha antes do menu), e o Saints Row 2 so roda com dgVoodoo (o DXVK crasha aos ~25 s,
    /// depois de o DLSS ja estar avaliando). Nao da para deduzir qual serve sem abrir o jogo.
    /// </summary>
    public Dictionary<string, string> D3d9Translator { get; set; } = new();
    /// <summary>Manually added game folders.</summary>
    public List<string> ManualGameDirs { get; set; } = new();

    /// <summary>Idioma da interface, como tag BCP-47 ("pt-BR", "en"). Vazio ou ausente
    /// significa "seguir o Windows", que e o padrao e o que quase todo mundo quer.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// O config.json existe mas nao pode ser LIDO nesta sessao (handle preso por outro processo,
    /// sem permissao). Essa instancia entao so carrega os padroes, e gravar padroes por cima de
    /// um arquivo intacto e exatamente a perda que o Save atomico existe para impedir — por isso
    /// Save recusa enquanto for verdade. Corrupcao de conteudo (JSON invalido) e outro caso: ai
    /// o backup .corrupt fica e os padroes podem ser gravados.
    /// </summary>
    [JsonIgnore]
    public bool NotLoaded { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static LauncherConfig Load()
    {
        if (!File.Exists(AppPaths.ConfigPath)) return new LauncherConfig();

        string json;
        try
        {
            json = File.ReadAllText(AppPaths.ConfigPath);
        }
        catch (FileNotFoundException) { return new LauncherConfig(); }
        catch (DirectoryNotFoundException) { return new LauncherConfig(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Falha de LEITURA, nao de conteudo: o arquivo continua bom no disco (a CLI no meio
            // do File.Replace, antivirus ou backup segurando o handle). Devolver padroes como se
            // fossem a configuracao seria mentira, e o Save seguinte gravava a mentira por cima
            // do config real — sem .corrupt, porque a copia falhava pelo mesmo motivo.
            Log.Warn($"config load FALHOU (I/O, arquivo mantido intacto): {ex.Message}");
            return new LauncherConfig { NotLoaded = true };
        }

        try
        {
            return JsonSerializer.Deserialize<LauncherConfig>(json) ?? new();
        }
        catch (Exception ex)
        {
            // corrupt config: keep a copy instead of silently discarding the user's settings
            Log.Warn($"config load FALHOU (mantendo backup): {ex.Message}");
            try { File.Copy(AppPaths.ConfigPath, AppPaths.ConfigPath + ".corrupt", overwrite: true); }
            catch { }
        }
        return new LauncherConfig();
    }

    private static readonly object SaveGate = new();

    /// <summary>Atomic save: write to a temp file then swap, so a crash/full disk mid-write
    /// can never leave a truncated config.json (which Load would silently reset to defaults,
    /// losing the user's nits profile and pinned exes).</summary>
    public void Save() => TrySave();

    /// <summary>O mesmo que Save, dizendo se o arquivo foi de fato gravado.</summary>
    public bool TrySave()
    {
        if (NotLoaded)
        {
            Log.Warn("config save RECUSADO: o config.json nao pode ser lido nesta sessao, e gravar agora sobrescreveria o arquivo do usuario com os padroes");
            return false;
        }
        try
        {
            lock (SaveGate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var json = JsonSerializer.Serialize(this, JsonOpts);
                var temp = AppPaths.ConfigPath + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(AppPaths.ConfigPath)) File.Replace(temp, AppPaths.ConfigPath, null);
                else File.Move(temp, AppPaths.ConfigPath);
            }
            return true;
        }
        catch (Exception ex) { Log.Warn($"config save: {ex.Message}"); return false; }
    }
}
