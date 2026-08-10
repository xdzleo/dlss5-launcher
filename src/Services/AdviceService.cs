using System.Text.RegularExpressions;

namespace RenoDXLauncher.Services;

public enum InGameHdr { Unknown, Enable, Disable }

/// <summary>A single high-signal recommendation extracted from the free-text notes.</summary>
public record Advice(string Icon, string Text, AdviceKind Kind);

public enum AdviceKind { HdrOff, HdrOn, Renderer, AntiCheat, Deprecated, Action }

/// <summary>
/// Turns the loose per-game note text (wiki hover notes, UE/Unity Notes column, RHI gameNotes)
/// into structured, prominent recommendations — above all whether the game's OWN HDR option
/// should be ON or OFF, which is the setting users most often get wrong.
/// </summary>
public static partial class AdviceService
{
    // "disable in-game HDR", "disable the game's native HDR", "in-game HDR ... off"
    [GeneratedRegex(@"(disabl\w*|turn\s*off)[^.]*\b(in[\s-]?game|native|game'?s)\b[^.]*\bhdr\b"
        + @"|\b(in[\s-]?game|native)\b[^.]*\bhdr\b[^.]*\b(off|disabl\w*)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex HdrDisableRegex();

    // "HDR On in-game", "enable in-game HDR", "in-game HDR must be on", "enable that too"
    [GeneratedRegex(@"\bhdr\b[^.]*\bon\b[^.]*\b(in[\s-]?game)\b"
        + @"|\b(in[\s-]?game)\b[^.]*\bhdr\b[^.]*\b(on|enabl\w*|must be on|required)\b"
        + @"|enabl\w*[^.]*\b(in[\s-]?game\s+)?hdr\b"
        + @"|in[\s-]?game hdr option, enable"
        + @"|enable that too",
        RegexOptions.IgnoreCase)]
    private static partial Regex HdrEnableRegex();

    // strip the Windows-side "disable AutoHDR / RTX HDR" advice so it never reads as in-game HDR
    [GeneratedRegex(@"(auto[\s-]?hdr|rtx\s*hdr)", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsHdrRegex();

    [GeneratedRegex(@"\b(dx\s*\d{1,2}|directx\s*\d{1,2}|vulkan|d3d1[012])\b", RegexOptions.IgnoreCase)]
    private static partial Regex RendererRegex();

    [GeneratedRegex(@"\b(anti[\s-]?cheat|easy anti[\s-]?cheat|\beac\b|battleye)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AntiCheatRegex();

    [GeneratedRegex(@"\b(deprecat\w*|abandon\w*|no longer maintained|not maintained)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeprecatedRegex();

    /// <summary>
    /// Curated in-game HDR recommendation for popular titles whose requirement lives only on
    /// Nexus/Discord (not in any machine-readable source we can fetch). Keyed by normalized name.
    /// Sources: RenoDX wiki guides, hdrmods.com, per-game Nexus pages. Only high-confidence entries.
    /// </summary>
    private static readonly Dictionary<string, InGameHdr> Curated = BuildCurated();

    private static Dictionary<string, InGameHdr> BuildCurated()
    {
        var d = new Dictionary<string, InGameHdr>();
        void On(string name) => d[MatchService.Normalize(name)] = InGameHdr.Enable;
        void Off(string name) => d[MatchService.Normalize(name)] = InGameHdr.Disable;
        // native-HDR games the mod fixes → in-game HDR must be ON
        On("Cyberpunk 2077");
        On("Elden Ring");
        On("Elden Ring Nightreign");
        On("The Witcher 3 Wild Hunt");
        On("Red Dead Redemption 2");
        On("Diablo IV");
        On("Resident Evil 4");
        // games where the mod does the HDR and native HDR must be OFF
        Off("Stellar Blade");
        Off("Deep Rock Galactic");
        Off("Final Fantasy XVI");
        return d;
    }

    /// <summary>Detect the in-game HDR recommendation from all note text combined,
    /// falling back to the curated table (by normalized game name) when notes are silent.</summary>
    public static InGameHdr DetectHdr(string? noteText, string? gameName = null)
    {
        if (!string.IsNullOrWhiteSpace(noteText))
        {
            // remove Windows-HDR sentences so "disable AutoHDR/RTX HDR" isn't misread
            var text = WindowsHdrRegex().Replace(noteText, " ");
            if (HdrDisableRegex().IsMatch(text)) return InGameHdr.Disable;
            if (HdrEnableRegex().IsMatch(text)) return InGameHdr.Enable;
        }
        if (gameName != null && Curated.TryGetValue(MatchService.Normalize(gameName), out var c))
            return c;
        return InGameHdr.Unknown;
    }

    /// <summary>Build the ordered list of prominent recommendations for a game.</summary>
    /// <param name="noteText">All note sources joined.</param>
    /// <param name="isNativeHdr">Whether RHI flags the game as native-HDR.</param>
    /// <param name="gameName">Game name, for the curated in-game-HDR fallback table.</param>
    public static List<Advice> Build(string? noteText, bool isNativeHdr, string? gameName = null)
    {
        var result = new List<Advice>();
        var text = noteText ?? "";

        var hdr = DetectHdr(text, gameName);
        // native-HDR games with no explicit note almost always want in-game HDR ON
        if (hdr == InGameHdr.Unknown && isNativeHdr) hdr = InGameHdr.Enable;

        if (hdr == InGameHdr.Disable)
            result.Add(new Advice("", "HDR do jogo: DESLIGAR (o mod faz o HDR; ligar os dois estoura a imagem)", AdviceKind.HdrOff));
        else if (hdr == InGameHdr.Enable)
            result.Add(new Advice("", "HDR do jogo: LIGAR (o mod corrige o HDR nativo — precisa dele ligado)", AdviceKind.HdrOn));

        if (DeprecatedRegex().IsMatch(text))
            result.Add(new Advice("", "Mod descontinuado/abandonado — pode não funcionar com o jogo/ReShade atuais", AdviceKind.Deprecated));

        if (AntiCheatRegex().IsMatch(text))
            result.Add(new Advice("", "Anti-cheat: risco em online — jogue offline ou desative o anti-cheat", AdviceKind.AntiCheat));

        // surface a required renderer only when the note says it "must"/"requires"/"only" run in one
        var rm = RendererRegex().Match(text);
        if (rm.Success && Regex.IsMatch(text, @"\b(must|requires?|only|needs? to)\b", RegexOptions.IgnoreCase))
            result.Add(new Advice("", $"Rode o jogo em {rm.Value.ToUpperInvariant().Replace(" ", "")} (exigido pelo mod)", AdviceKind.Renderer));

        return result;
    }
}
