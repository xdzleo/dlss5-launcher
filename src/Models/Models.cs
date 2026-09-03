namespace RenoDXLauncher.Models;

public enum GameStore { Steam, Epic, Gog, Xbox, Ubisoft, EA, BattleNet, Rockstar, Folder, Manual }

/// <summary>An installed game found by one of the store scanners.</summary>
public class GameInfo
{
    public required string Name { get; init; }
    public required string InstallDir { get; init; }
    public required GameStore Store { get; init; }
    /// <summary>Store-specific id (Steam appid, Epic CatalogItemId, GOG gameID, Xbox package name).</summary>
    public string? AppId { get; init; }
    public int? SteamAppId { get; init; }
    /// <summary>Executable hint from store metadata (Epic LaunchExecutable, Xbox gamelaunchhelper).</summary>
    public string? ExeHint { get; init; }
    public string? LocalCoverPath { get; init; }
}

public enum ModKind { Dedicated, UnrealEngine, UnityEngine }

/// <summary>One entry of the RenoDX mod catalog (games-index.json + wiki Mods.md merged).</summary>
public class CatalogEntry
{
    public required string GameName { get; init; }
    public string NormalizedName { get; set; } = "";
    /// <summary>All normalized names this entry answers to (title variants, games-index aliases,
    /// parenthetical-stripped forms, combined-row splits).</summary>
    public HashSet<string> NormalizedAliases { get; } = new(StringComparer.Ordinal);
    public ModKind Kind { get; init; }
    public string? Maintainer { get; set; }
    /// <summary>Direct .addon64/.addon32 download URL (snapshot build), if available.</summary>
    public string? DownloadUrl { get; set; }
    /// <summary>32 or 64, from artifact arch / URL extension. 0 when unknown.</summary>
    public int AddonBits { get; set; }
    /// <summary>renodx-&lt;slug&gt;.addonXX — used to look up the settings manifest.</summary>
    public string? Slug { get; init; }
    public int? SteamAppId { get; set; }
    public string? NexusUrl { get; set; }
    /// <summary>Fallback link (discussion/Discord) for rows without direct download or Nexus page.</summary>
    public string? InfoUrl { get; set; }
    /// <summary>true = marked working; false = in progress / unknown.</summary>
    public bool Working { get; set; }
    /// <summary>Notes from the wiki (hover note or UE/Unity Notes column).</summary>
    public string? Note { get; set; }
    /// <summary>Every piece of per-game guidance found, with provenance. <see cref="Note"/> is the
    /// raw wiki text kept for the advice extractor; this is what the user actually reads.</summary>
    public List<ModNote> Notes { get; } = new();
    /// <summary>GitHub discussion linked from the game's NAME cell — for several games this is the
    /// only place the prerequisites are written down.</summary>
    public string? DiscussionUrl { get; set; }
}

/// <summary>Where a piece of guidance came from. Shown to the user, because "the wiki says" and
/// "the mod's author wrote in the code" carry different weight.</summary>
public enum NoteSource { Wiki, WikiEngine, WikiLegend, Rhi, ModSource, Launcher }

public enum NoteKind { Info, Warning, Step, Preset }

/// <summary>A link kept WITH its destination — the old note pipeline threw URLs away and left
/// text like "see here" pointing nowhere.</summary>
public record NoteLink(string Label, string Url);

/// <summary>One piece of guidance about a game.</summary>
public record ModNote(
    NoteSource Source,
    NoteKind Kind,
    string? Title,
    string Text,
    IReadOnlyList<NoteLink>? Links = null,
    /// <summary>Verbatim block (an .ini snippet, a launch argument) rendered monospaced.</summary>
    string? Preformatted = null,
    /// <summary>WHERE the user has to act: "NO JOGO", "OVERLAY RENODX (Home)", "PASTA DO JOGO".
    /// Without this every instruction reads as if it happened in the same place.</summary>
    string? Location = null,
    /// <summary>Identificador estavel, nunca exibido. Existe para o codigo reconhecer uma nota
    /// especifica sem comparar o Title, que e texto traduzido e muda com o idioma.</summary>
    string? Id = null)
{
    /// <summary>Text with layout collapsed, for dedup against other sources. Symbols that change
    /// the MEANING stay: dropping them made "Vanilla+ SDR" collide with "Vanilla SDR" and one of
    /// Valheim's four presets silently vanish.</summary>
    public string DedupKey => new string(
        Text.Where(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '/' or '.' or '%')
            .Select(char.ToLowerInvariant)
            .ToArray());

    // ----- o texto, quebrado no que ele ja e -----
    //
    // Estas notas sao escritas por gente, numa wiki, em markdown de rascunho: uma frase de
    // abertura, as vezes um "IMPORTANT:" na frente, e uma lista com hifens. Tudo isso chegava na
    // tela como UM paragrafo, com os hifens no meio do corrido. Ler uma lista escrita em linha e
    // trabalho que a interface pode fazer pela pessoa.
    //
    // A quebra e feita uma vez e guardada: bindings releem propriedade muitas vezes, e isto e
    // manipulacao de string.

    private (string Lead, IReadOnlyList<string> Itens, string? Destaque)? _partes;
    private (string Lead, IReadOnlyList<string> Itens, string? Destaque) Partes => _partes ??= Quebrar(Text);

    /// <summary>O texto corrido, sem a lista e sem a palavra de destaque.</summary>
    public string Lead => Partes.Lead;

    /// <summary>Os itens de lista, ja sem o marcador.</summary>
    public IReadOnlyList<string> Itens => Partes.Itens;

    /// <summary>"IMPORTANT", "NOTE"... quando a nota comeca assim. Vira selo, e sai do texto.</summary>
    public string? Destaque => Partes.Destaque;

    /// <summary>O cabecalho do cartao tem algo a mostrar?</summary>
    public bool TemCabecalho =>
        !string.IsNullOrWhiteSpace(Location) || !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(Destaque);

    /// <summary>Uma nota que pede ACAO, e nao uma que so informa. Decide se ela fica a vista ou
    /// atras do bloco recolhido de detalhes.</summary>
    public bool EhAcionavel =>
        Kind is NoteKind.Step or NoteKind.Warning
        || Destaque is not null
        || Itens.Count > 0
        || Preformatted is not null;

    /// <summary>Palavras com que uma nota se anuncia. So contam no COMECO do texto, seguidas de
    /// dois-pontos — no meio de uma frase sao palavras comuns.</summary>
    private static readonly string[] Destaques =
        ["important", "importante", "note", "nota", "warning", "aviso", "atencao", "atenção", "tip", "dica"];

    private static (string, IReadOnlyList<string>, string?) Quebrar(string texto)
    {
        var corpo = (texto ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        string? destaque = null;

        var doisPontos = corpo.IndexOf(':');
        if (doisPontos is > 0 and < 16)
        {
            var cabeca = corpo[..doisPontos].Trim();
            if (Destaques.Contains(cabeca, StringComparer.OrdinalIgnoreCase))
            {
                destaque = cabeca.ToUpperInvariant();
                corpo = corpo[(doisPontos + 1)..].TrimStart();
            }
        }

        var lead = new List<string>();
        var itens = new List<string>();
        foreach (var bruta in corpo.Split('\n'))
        {
            var linha = bruta.Trim();
            if (linha.Length == 0) continue;
            var item = TirarMarcador(linha);
            if (item is not null) itens.Add(item);
            // Uma linha comum DEPOIS de a lista comecar e continuacao do ultimo item, e nao um
            // paragrafo novo: a wiki quebra item longo em duas linhas.
            else if (itens.Count > 0) itens[^1] = itens[^1] + " " + linha;
            else lead.Add(linha);
        }
        return (string.Join(" ", lead), itens, destaque);
    }

    /// <summary>A linha e um item de lista? Devolve o texto sem o marcador, ou null.</summary>
    private static string? TirarMarcador(string linha)
    {
        if (linha.Length >= 2 && linha[0] is '-' or '*' or '•' or '·' or '–' && char.IsWhiteSpace(linha[1]))
            return linha[2..].Trim();
        // "1. ", "2) " — lista numerada. O numero e recriado pela interface, entao sai daqui.
        var i = 0;
        while (i < linha.Length && char.IsDigit(linha[i])) i++;
        if (i is > 0 and <= 2 && i + 1 < linha.Length && linha[i] is '.' or ')'
            && char.IsWhiteSpace(linha[i + 1]))
            return linha[(i + 2)..].Trim();
        return null;
    }
}

/// <summary>State of ReShade + RenoDX inside one game's deploy directory.</summary>
public class ModState
{
    /// <summary>Directory that contains the game's rendering exe (where ReShade + addon live).</summary>
    public required string TargetDir { get; init; }
    public string? ExePath { get; init; }
    public bool ReShadePresent { get; set; }
    public string? ReShadeDllName { get; set; }
    public string? ReShadeVersion { get; set; }
    /// <summary>Full path of the renodx addon file (enabled or disabled variant), if present.</summary>
    public string? AddonPath { get; set; }
    public bool AddonEnabled { get; set; }
    public string IniPath => System.IO.Path.Combine(TargetDir, "ReShade.ini");
}

/// <summary>One RenoDX setting definition (from the embedded settings manifest).</summary>
public class SettingDef
{
    public required string Key { get; init; }
    /// <summary>float | int | bool</summary>
    public string Type { get; init; } = "float";
    public string? Label { get; init; }
    public string? Section { get; init; }

    /// <summary>Secao do .ini onde este valor vive. Null = a secao de preset do mod. Existe porque
    /// addons genericos (o neural, por exemplo) tem bloco proprio e nao participam do preset.</summary>
    public string? IniSection { get; init; }
    public string? Tooltip { get; init; }
    public double? Default { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>Combo labels for int settings (index = value).</summary>
    public IReadOnlyList<string>? Labels { get; init; }
    public bool IsGlobal { get; init; }
    /// <summary>TEXT/LABEL/BULLET block: not a knob, but instructions the mod's author wrote for
    /// the player. These used to be dropped, which is why games whose author explained everything
    /// in the overlay showed up in the launcher as "no adjustable settings".</summary>
    public bool IsInstruction { get; init; }
    /// <summary>BUTTON block: values the author applies at once (a calibrated look).</summary>
    public IReadOnlyDictionary<string, double>? PresetValues { get; init; }
}
