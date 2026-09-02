using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>
/// Reads a mod's settings straight from its source when the embedded manifest does not know it
/// (a mod published after this build). Each RenoDX mod declares its options as C++ designated
/// initializers — <c>Setting{ .key = "ToneMapPeakNits", .default_value = 1000.f, ... }</c> — in
/// src/games/&lt;slug&gt;/*.cpp of the maintainer's repo, so the source IS the schema.
///
/// Mirrors tools/extract_settings_manifest.py, which builds the offline manifest the same way.
/// </summary>
public static partial class SettingsFetcher
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    [GeneratedRegex(@"\.(\w+)\s*=")]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"^""(.*)""$", RegexOptions.Singleline)]
    private static partial Regex StringRegex();

    [GeneratedRegex(@"^-?(?:[0-9]+\.?[0-9]*|\.[0-9]+)f?$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex QuotedRegex();

    /// <summary>Fetch and parse a mod's settings, or null when unavailable.</summary>
    public static async Task<IReadOnlyList<SettingDef>?> TryFetchAsync(CatalogEntry entry)
    {
        if (entry.Slug is null || ModHistoryService.RepoOf(entry) is not var (owner, repo))
            return null;

        var cachePath = Path.Combine(AppPaths.DataDir, "modsettings", $"{owner}_{repo}_{entry.Slug}.cpp");
        try
        {
            string source;
            if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheTtl)
            {
                source = await File.ReadAllTextAsync(cachePath);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
                source = "";
                // main is the usual default branch; a couple of forks still use master
                foreach (var branch in new[] { "main", "master" })
                {
                    var url = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/src/games/{entry.Slug}/addon.cpp";
                    try
                    {
                        source = await http.GetStringAsync(url);
                        if (source.Length > 0) break;
                    }
                    catch { /* try the next branch */ }
                }
                if (source.Length > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    await File.WriteAllTextAsync(cachePath, source);
                }
                else if (File.Exists(cachePath))
                {
                    // Offline, ou o GitHub cortou o IP: o cache vencido continua sendo o fonte
                    // do mod, e os sliders de uma semana atras valem mais que "configuracoes
                    // indisponiveis" — que era o que o usuario via depois de 7 dias sem rede,
                    // com o fonte parseado ali no disco. O historico ja fazia essa queda.
                    Log.Warn($"settings de {entry.Slug}: sem rede, usando o cache vencido");
                    source = await File.ReadAllTextAsync(cachePath);
                }
                else return null;
            }

            var parsed = Parse(source);
            Log.Info($"settings de {entry.Slug} lidas do fonte ({parsed.Count})");
            return parsed;
        }
        catch (Exception ex)
        {
            Log.Warn($"fetch settings {entry.Slug}: {ex.Message}");
            // a mesma queda para o cache vencido quando a falha veio antes do laco de fetch
            try
            {
                if (File.Exists(cachePath))
                {
                    Log.Warn($"settings de {entry.Slug}: usando o cache vencido apos falha");
                    return Parse(await File.ReadAllTextAsync(cachePath));
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>Parse every Setting{...} initializer in a mod's source.</summary>
    public static List<SettingDef> Parse(string source)
    {
        var result = new List<SettingDef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in SettingBlocks(source))
        {
            var def = ParseSetting(block);
            if (def is null) continue;
            // instruction blocks share the empty key on purpose — deduping them by key would
            // keep only the first paragraph of the author's explanation
            if (def.IsInstruction) result.Add(def);
            else if (seen.Add(def.Key)) result.Add(def);
        }
        return result;
    }

    /// <summary>Blank out // and /* */ comments, preserving length and line breaks so every other
    /// offset still lines up. Without this, mods that keep an old Setting block commented out
    /// (akuru-q's BMW keeps three contradictory status messages in there) publish dead text as
    /// live instructions.</summary>
    internal static string StripComments(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c == '"' || c == '\'')
            {
                char quote = c;
                i++;
                while (i < chars.Length && chars[i] != quote)
                {
                    if (chars[i] == '\\') i++;
                    i++;
                }
                continue;
            }
            if (c != '/' || i + 1 >= chars.Length) continue;
            if (chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
            }
            else if (chars[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? chars.Length : end + 2;
                for (; i < end; i++) if (chars[i] != '\n') chars[i] = ' ';
                i--;
            }
        }
        return new string(chars);
    }

    /// <summary>Positions inside a string literal, so a field regex cannot fire on a '.' that is
    /// part of the author's prose. Hitman's note contains ".25" and ". Formula" and used to be
    /// truncated at the first one, losing 85% of the text.</summary>
    private static bool[] LiteralMask(string block)
    {
        var mask = new bool[block.Length];
        for (int i = 0; i < block.Length; i++)
        {
            if (block[i] != '"') continue;
            int start = i++;
            while (i < block.Length && block[i] != '"')
            {
                if (block[i] == '\\') i++;
                i++;
            }
            for (int j = start; j <= Math.Min(i, block.Length - 1); j++) mask[j] = true;
        }
        return mask;
    }

    /// <summary>Brace-balanced bodies of each <c>Setting{ ... }</c>, skipping string literals
    /// so a brace inside a tooltip never ends the block early.</summary>
    private static IEnumerable<string> SettingBlocks(string source)
    {
        var text = StripComments(source);
        foreach (Match m in Regex.Matches(text, @"Setting\s*\{"))
        {
            int depth = 1, i = m.Index + m.Length, start = i;
            while (i < text.Length && depth > 0)
            {
                char c = text[i];
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == '"')
                {
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\') i++;
                        i++;
                    }
                }
                i++;
            }
            if (depth == 0) yield return text[start..(i - 1)];
        }
    }

    private static SettingDef? ParseSetting(string block)
    {
        string? key = null, type = null, label = null, section = null, tooltip = null;
        double? def = null, min = null, max = null;
        List<string>? labels = null;
        bool isGlobal = false;

        // field matches that land INSIDE a string literal are the author's prose, not a field
        var inLiteral = LiteralMask(block);
        var fields = FieldRegex().Matches(block).Where(m => !inLiteral[m.Index]).ToList();
        for (int i = 0; i < fields.Count; i++)
        {
            var name = fields[i].Groups[1].Value;
            int start = fields[i].Index + fields[i].Length;
            int end = i + 1 < fields.Count ? fields[i + 1].Index : block.Length;
            var raw = block[start..end].Trim().TrimEnd(',').Trim();

            switch (name)
            {
                case "key": key = AsString(raw); break;
                case "value_type":
                    type = raw.Contains("INTEGER") ? "int"
                         : raw.Contains("BOOLEAN") ? "bool"
                         : raw.Contains("BUTTON") ? "button"
                         : raw.Contains("LABEL") || raw.Contains("TEXT") || raw.Contains("BULLET") ? "label"
                         : raw.Contains("FLOAT") ? "float" : type;
                    break;
                case "default_value": def = AsNumber(raw); break;
                case "label": label = AsString(raw); break;
                case "section": section = AsString(raw); break;
                case "tooltip": tooltip = AsString(raw); break;
                case "min": min = AsNumber(raw); break;
                case "max": max = AsNumber(raw); break;
                case "labels": labels = AsLabels(raw); break;
                case "is_global": isGlobal = raw.StartsWith("true"); break;
            }
        }

        type ??= "float";

        // TEXT / LABEL / BULLET / BUTTON blocks carry no key because they are not knobs — they are
        // what the mod's author WROTE FOR THE PLAYER inside the overlay. Dropping them is why
        // games like DOOM: The Dark Ages showed up as "the author left the values fixed" when the
        // author had in fact written the whole configuration procedure in there.
        if (type is "button" or "label")
        {
            var text = label ?? tooltip;
            if (text is null) return null;
            var values = type == "button" ? PresetValues(block) : null;
            // a button whose only job is to open a URL is a link, not guidance ("Get more RenoDX
            // mods!" was showing up as an instruction on the user's S.T.A.L.K.E.R. 2)
            if (values is null && OpensUrlRegex().IsMatch(block)) return null;
            text = DropSocialLines(text);
            if (text.Length == 0 || IsBoilerplate(text, tooltip, section, values != null)) return null;
            return new SettingDef
            {
                Key = "",
                Type = type,
                Label = label is null ? null : DropSocialLines(label),
                Section = section,
                Tooltip = tooltip,
                IsInstruction = true,
                PresetValues = values,
            };
        }

        if (string.IsNullOrEmpty(key)) return null;

        return new SettingDef
        {
            Key = key,
            Type = type,
            Label = label,
            Section = section,
            Tooltip = tooltip,
            Default = def,
            Min = min,
            Max = max,
            Labels = labels,
            IsGlobal = isGlobal,
        };
    }

    /// <summary>Social links and build stamps are not guidance — without this filter every game
    /// would gain five lines of "Author's Ko-Fi" and "This build was compiled on ...".</summary>
    [GeneratedRegex(@"LaunchURL|https?://|ShellExecute", RegexOptions.IgnoreCase)]
    private static partial Regex OpensUrlRegex();

    /// <summary>Sections whose whole point is to explain the game. Never discarded — the word
    /// "discord" inside one of these used to delete the entire block, and with it warnings like
    /// yumia1's "NVIDIA GPUs only -- AMD/Intel are unsupported".</summary>
    private static readonly string[] GuidanceSections = { "instructions", "notes", "read me", "readme", "how to" };

    /// <summary>Drop only the LINE that is social/build noise, keeping the rest of the block.</summary>
    private static string DropSocialLines(string text)
    {
        var kept = text.Split('\n')
            .Where(line => !Regex.IsMatch(line,
                @"ko-?fi|patreon|paypal|buymeacoffee|twitter|donate|join .*(discord|server)"
                + @"|was compiled on|^\s*build\s*(date)?\s*[:\-]|^\s*version\s*[:\-]",
                RegexOptions.IgnoreCase))
            .ToList();
        return string.Join("\n", kept).Trim();
    }

    private static bool IsBoilerplate(string label, string? tooltip, string? section, bool isPreset)
    {
        bool guidanceSection = section is not null
            && GuidanceSections.Contains(section.Trim().ToLowerInvariant());
        if (guidanceSection) return false;   // the author labelled it as instructions; believe them

        // authors park their social buttons in a "Links" section — none of it is guidance
        if (section is not null && section.Equals("Links", StringComparison.OrdinalIgnoreCase))
            return true;
        // a preset carries values, so it is an action regardless of how it is named
        if (isPreset) return false;
        // "Reset All" restores the mod's own defaults and "Version:" is a build stamp — overlay
        // furniture, not something the player is being told to do
        if (Regex.IsMatch(label, @"^\s*(reset(\s+all)?|version:?|credits?|github|more mods)\s*$",
                RegexOptions.IgnoreCase))
            return true;
        // credits: no imperative verb, and a "<role> by <Name>" / "thanks to" shape
        if (!HasImperative(label)
            && Regex.IsMatch(label,
                @"\b(by|thanks to|credits?|maintained|shout-?out|bug hunter|framework)\b",
                RegexOptions.IgnoreCase))
            return true;
        var all = (label + " " + tooltip).ToLowerInvariant().Trim();
        return all is "github" or "more mods" or "discord" or "ko-fi";
    }

    /// <summary>Does this text tell the player to DO something? Credits never do.</summary>
    private static bool HasImperative(string text) => Regex.IsMatch(text,
        @"\b(set|use|enable|disable|turn|toggle|adjust|change|open|close|restart|install|update"
        + @"|make sure|leave|switch|move|press|click|select|apply|avoid|requires?|must|should|need)\b",
        RegexOptions.IgnoreCase);

    [GeneratedRegex(@"\{\s*""(\w+)""\s*,\s*(-?[\d.]+)f?\s*\}")]
    private static partial Regex PresetPairRegex();

    [GeneratedRegex(@"UpdateSetting\(\s*""(\w+)""\s*,\s*(-?[\d.]+)f?\s*\)")]
    private static partial Regex UpdateSettingRegex();

    /// <summary>Values a BUTTON applies at once — the look the author calibrated. Both spellings
    /// occur in the wild: a brace-initialized list and explicit UpdateSetting calls.</summary>
    private static IReadOnlyDictionary<string, double>? PresetValues(string block)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Match m in PresetPairRegex().Matches(block).Concat(UpdateSettingRegex().Matches(block)))
            if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                values[m.Groups[1].Value] = v;
        return values.Count > 0 ? values : null;
    }

    /// <summary>C++ concatenates adjacent string literals, and mod authors use that to write
    /// multi-line instructions. Reading only the first literal truncated them mid-sentence.</summary>
    private static string? AsString(string raw)
    {
        var literals = QuotedRegex().Matches(raw).Select(m => Unescape(m.Groups[1].Value)).ToList();
        if (literals.Count == 0) return null;
        var joined = string.Concat(literals).Trim();
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Undo C++ escapes in ONE left-to-right pass. Chained Replace calls are wrong twice
    /// over: they never handled \r (so S.T.A.L.K.E.R. 2's note showed a literal "\r" at the end of
    /// every line) and they rewrite the output of earlier replacements.</summary>
    private static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char n = s[++i];
            sb.Append(n switch
            {
                'n' => "\n", 'r' => "", 't' => "  ", '0' => "",
                '\\' => "\\", '"' => "\"", '\'' => "'",
                _ => n.ToString(),
            });
        }
        return sb.ToString();
    }

    private static double? AsNumber(string raw)
    {
        raw = raw.Trim();
        if (!NumberRegex().IsMatch(raw)) return null;
        return double.TryParse(raw.TrimEnd('f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    private static List<string>? AsLabels(string raw)
    {
        if (!raw.StartsWith('{')) return null;
        var list = QuotedRegex().Matches(raw).Select(m => m.Groups[1].Value).ToList();
        return list.Count > 0 ? list : null;
    }
}
