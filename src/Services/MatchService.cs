using System.Text;
using System.Text.RegularExpressions;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>Matches installed games against catalog entries by normalized name.</summary>
public static partial class MatchService
{
    [GeneratedRegex(@"\b(the|a|an|of|and)\b")]
    private static partial Regex StopWordsRegex();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParentheticalRegex();

    [GeneratedRegex(
        @"\s+(game of the year( edition)?|goty( edition)?|(definitive|enhanced|complete|deluxe|ultimate|gold|premium|legendary|digital|anniversary|special|standard)( edition)?|remastered|remaster|redux|directors? cut|hd)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EditionSuffixRegex();

    /// <summary>Aggressive normalization: lowercase, strip ®™© and diacritics and parentheticals,
    /// &amp; → and, drop stop words and all non-alphanumerics.</summary>
    public static string Normalize(string name)
    {
        var s = name.ToLowerInvariant();
        s = ParentheticalRegex().Replace(s, " ");
        s = s.Replace("&", " and ").Replace("+", " plus ");
        s = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else sb.Append(' ');
        }
        s = StopWordsRegex().Replace(sb.ToString(), " ");
        return Regex.Replace(s, @"\s+", "");
    }

    /// <summary>Installed-game name with trailing edition qualifiers removed (word-level, before collapse).</summary>
    public static string StripEditionSuffix(string name)
    {
        var s = name;
        for (int i = 0; i < 3; i++)
        {
            var next = EditionSuffixRegex().Replace(s, "");
            if (next == s) break;
            s = next;
        }
        return s;
    }

    /// <summary>Best catalog match for an installed game, or null.
    /// Exact normalized-name/alias equality only (optionally after stripping edition suffixes
    /// from the installed side) — no prefix matching: "Dishonored 2" must never match "Dishonored".</summary>
    public static CatalogEntry? FindMatch(GameInfo game, IReadOnlyList<CatalogEntry> catalog)
    {
        var n = Normalize(game.Name);
        if (n.Length == 0) return null;

        var exact = catalog.Where(e => e.NormalizedAliases.Contains(n)).ToList();
        if (exact.Count > 0) return PickBest(exact);

        var stripped = Normalize(StripEditionSuffix(game.Name));
        if (stripped.Length > 0 && stripped != n)
        {
            var byEdition = catalog.Where(e => e.NormalizedAliases.Contains(stripped)).ToList();
            if (byEdition.Count > 0) return PickBest(byEdition);
        }
        return null;
    }

    private static CatalogEntry PickBest(List<CatalogEntry> candidates) =>
        candidates.OrderBy(e => e.Kind == ModKind.Dedicated ? 0 : 1)
                  .ThenByDescending(e => e.Working)
                  .ThenByDescending(e => e.DownloadUrl != null)
                  .First();
}
