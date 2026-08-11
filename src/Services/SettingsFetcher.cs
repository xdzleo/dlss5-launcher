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
                if (source.Length == 0) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(cachePath, source);
            }

            var parsed = Parse(source);
            Log.Info($"settings de {entry.Slug} lidas do fonte ({parsed.Count})");
            return parsed;
        }
        catch (Exception ex)
        {
            Log.Warn($"fetch settings {entry.Slug}: {ex.Message}");
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

    /// <summary>Brace-balanced bodies of each <c>Setting{ ... }</c>, skipping string literals
    /// so a brace inside a tooltip never ends the block early.</summary>
    private static IEnumerable<string> SettingBlocks(string text)
    {
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

        var fields = FieldRegex().Matches(block);
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
            if (text is null || IsBoilerplate(text, tooltip, section)) return null;
            return new SettingDef
            {
                Key = "",
                Type = type,
                Label = label,
                Section = section,
                Tooltip = tooltip,
                IsInstruction = true,
                PresetValues = type == "button" ? PresetValues(block) : null,
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
    private static bool IsBoilerplate(string label, string? tooltip, string? section)
    {
        // authors park their social buttons in a "Links" section — none of it is guidance
        if (section is not null && section.Equals("Links", StringComparison.OrdinalIgnoreCase))
            return true;
        // "Reset"/"Reset All" restore the mod's own defaults and "Version:" is a build stamp —
        // both are overlay furniture, not something the player is being told to do
        if (Regex.IsMatch(label, @"^\s*(reset(\s+all)?|version:?|credits?|github|more mods)\s*$",
                RegexOptions.IgnoreCase))
            return true;
        // credits paragraphs: "Game mod by X, RenoDX Framework by Y", "Special thanks to Z"
        if (Regex.IsMatch(label, @"special thanks|framework by|^mod by ", RegexOptions.IgnoreCase))
            return true;
        var all = (label + " " + tooltip).ToLowerInvariant().Trim();
        return all is "github" or "more mods" or "discord" or "ko-fi"
            || all.Contains("ko-fi") || all.Contains("kofi") || all.Contains("patreon")
            || all.Contains("discord") || all.Contains("paypal") || all.Contains("github.com")
            || all.Contains("buymeacoffee") || all.Contains("twitter") || all.Contains("donate")
            || label.StartsWith("Build:", StringComparison.OrdinalIgnoreCase)
            || label.Contains("was compiled on", StringComparison.OrdinalIgnoreCase);
    }

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
        var literals = QuotedRegex().Matches(raw)
            .Select(m => m.Groups[1].Value.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\"))
            .ToList();
        if (literals.Count == 0) return null;
        var joined = string.Concat(literals).Trim();
        return joined.Length == 0 ? null : joined;
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
