using System.Text.RegularExpressions;
using RenoDXLauncher.Localization;

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
    /// <summary>
    /// WHERE the user has to act. An instruction that does not say whether it happens in the
    /// game's own menu or in the RenoDX overlay is half an instruction — but a WRONG label is
    /// worse than none: it sends the person to the wrong menu, they see nothing happen, and they
    /// conclude the mod is broken. So this only speaks when the text is unambiguous.
    ///
    /// Three rules earned by real notes that the first version got backwards:
    ///  - a place word inside a NEGATION means the opposite ("In-game HDR settings are disabled by
    ///    RenoDX, adjust brightness in the mod" is an OVERLAY instruction, and was labelled NO JOGO);
    ///  - a note that names BOTH places is a two-step procedure and gets no single label;
    ///  - "slider", "upgrade", "tone map" and "renodx" name THINGS, not places — a note can talk
    ///    about the game's own sliders. Only an explicit "in the mod / in the overlay / Settings
    ///    Mode / &lt;FORMAT&gt; Output Size" anchors the overlay.
    /// </summary>
    public static string? GuessLocation(string? text, string? section = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // A note is usually a list of steps, and the steps can live in different places. Judge
        // each clause on its own and only speak when the ones that say something all agree —
        // "Disable in-game HDR. B8G8R8A8_TYPELESS Output Size" is two places, so it gets none.
        var clauses = System.Text.RegularExpressions.Regex
            .Split(text, @"\n|(?<=[.;!?])\s+|,\s+(?=\w)")
            .Where(c => c.Trim().Length > 0);
        var found = clauses.Select(c => LocationOfClause(c, section))
            .Where(l => l != null)
            .Distinct()
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }

    private static readonly System.Text.RegularExpressions.RegexOptions Ic =
        System.Text.RegularExpressions.RegexOptions.IgnoreCase;

    /// <summary>The place named by ONE sentence, or null when it names none or both.</summary>
    private static string? LocationOfClause(string clause, string? section)
    {
        // "X is disabled by the mod", "doesn't do anything", "no longer": the place named is the
        // place NOT to touch
        bool negated = System.Text.RegularExpressions.Regex.IsMatch(clause,
            @"\b(disabled|overridden|ignored|no longer|does\s?n'?t|doesn't do anything|not needed"
            + @"|automatically|has no effect|inert)\b", Ic);

        bool inGame = System.Text.RegularExpressions.Regex.IsMatch(clause,
            @"\bin[\s-]?game\b|\bingame\b|\bgame'?s (own |native )?(hdr|brightness|contrast|gamma|menu|settings)"
            + @"|game settings|game menu|exclusive fullscreen", Ic);

        // the wiki writes resource upgrades telegraphically, with no verb at all:
        // "`B8G8R8A8_TYPELESS` `Output Size`" IS an instruction, and it is an overlay one
        bool upgradeToken = System.Text.RegularExpressions.Regex.IsMatch(clause,
            @"`?(output size|output ratio|any size)`?|\bswapchain proxy\b"
            + @"|\b[A-Z]\d{0,2}[A-Z]\d{0,2}[A-Z0-9_]{3,}(_UNORM|_FLOAT|_TYPELESS|_SRGB)\b", Ic);

        bool inOverlay = upgradeToken
            || System.Text.RegularExpressions.Regex.IsMatch(clause,
                @"\bin the (mod|addon|overlay)\b|\brenodx overlay\b|\bsettings mode\b"
                + @"|\bresource upgrades?\b|\bupgrade\s+`?[A-Z][A-Z0-9_]{4,}`?", Ic)
            || section is "Resource Upgrades" or "Color Grading Templates";

        // an imperative is what turns a mention into an instruction; "once loaded in game" is
        // narration and used to be read as "go to the game's menu"
        bool imperative = upgradeToken || System.Text.RegularExpressions.Regex.IsMatch(clause,
            @"\b(set|use|enable|disable|turn|toggle|adjust|change|open|leave|switch|move|press"
            + @"|click|select|apply|avoid|keep|must|should|requires?)\b", Ic);
        if (!imperative) return null;

        // a negated place is the place NOT to touch, so it stops being a signal
        if (negated)
        {
            if (inGame && inOverlay) return "OVERLAY RENODX (Home)";  // "in-game X is disabled, use the mod"
            return null;
        }
        if (inGame && inOverlay) return null;   // two-step procedure: no single label
        if (inGame) return "NO JOGO";
        if (inOverlay) return "OVERLAY RENODX (Home)";
        return null;
    }

    /// <summary>Characters that carry MEANING in a configuration instruction and must survive.
    /// "Settings Mode: Simple → Advanced" without the arrow is not an instruction any more.</summary>
    private static readonly Dictionary<int, string> Meaningful = new()
    {
        [0x2192] = "->", [0x2190] = "<-", [0x2194] = "<->", [0x21D2] = "=>",
        [0x2264] = "<=", [0x2265] = ">=", [0x2260] = "!=", [0x00D7] = "x",
        [0x2022] = "-",  [0x2013] = "-",  [0x2014] = "—",
        // ⛔ and 🚫 are handled in StripSymbols: their replacement is WORDS, so it has to be
        // resolved per call against the current language, not baked into this static table.
        [0x2705] = "[OK]", [0x2714] = "[OK]", [0x274C] = "[X]", [0x2716] = "[X]",
        // ⚠ and ℹ are decoration next to a note that ALREADY says "Atenção"/"Nota" — translating
        // them produced "ATENÇÃO: ATENÇÃO: UNREAL ENGINE MOD WARNINGS ATENÇÃO: ATENÇÃO:".
        [0x26A0] = "", [0x2139] = "",
        // arrows used as list decoration rather than as an operator
        [0x21A9] = "", [0x21AA] = "", [0x21B0] = "", [0x21B5] = "", [0x27A1] = "",
    };

    /// <summary>Remove pictographic characters that come from external note data (wiki/RHI):
    /// WPF renders them as boxes or wrong glyphs depending on which font resolves them.
    ///
    /// Order matters. The old version blanked everything from U+2190 to U+2BFF, which swallowed
    /// the arrows the wiki uses as the configuration operator. Meaning is translated FIRST, then
    /// what is left of the purely decorative ranges is dropped.</summary>
    public static string StripSymbols(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            int v = rune.Value;
            // the only meaningful symbol whose replacement is a translated word
            if (v is 0x26D4 or 0x1F6AB)
            {
                sb.Append(L.T("Common_DoNot_Prefix"));
                continue;
            }
            if (Meaningful.TryGetValue(v, out var replacement))
            {
                sb.Append(replacement);
                continue;
            }
            bool decorative =
                (v >= 0x2600 && v <= 0x27BF) ||   // dingbats, weather, misc symbols
                (v >= 0x2B00 && v <= 0x2BFF) ||   // arrows-supplement block used as bullets
                (v >= 0xFE00 && v <= 0xFE0F) ||   // variation selectors
                (v >= 0x1F000 && v <= 0x1FAFF) || // emoji planes
                v == 0x200D ||                    // zero-width joiner (emoji sequences)
                v == 0x00A0;                      // nbsp: breaks wrapping
            sb.Append(decorative ? " " : rune.ToString());
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"[ \t]{2,}", " ").Trim();
    }

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
            result.Add(new Advice("", L.T("Install_Advice_HdrOff"), AdviceKind.HdrOff));
        else if (hdr == InGameHdr.Enable)
            result.Add(new Advice("", L.T("Install_Advice_HdrOn"), AdviceKind.HdrOn));

        if (DeprecatedRegex().IsMatch(text))
            result.Add(new Advice("", L.T("Install_Advice_Deprecated"), AdviceKind.Deprecated));

        if (AntiCheatRegex().IsMatch(text))
            result.Add(new Advice("", L.T("Install_AntiCheat_Warning"), AdviceKind.AntiCheat));

        // surface a required renderer only when the note says it "must"/"requires"/"only" run in one
        var rm = RendererRegex().Match(text);
        if (rm.Success && Regex.IsMatch(text, @"\b(must|requires?|only|needs? to)\b", RegexOptions.IgnoreCase))
            result.Add(new Advice("", L.T("Install_Advice_Renderer",
                rm.Value.ToUpperInvariant().Replace(" ", "")), AdviceKind.Renderer));

        return result;
    }
}
