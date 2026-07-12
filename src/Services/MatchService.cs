using System.Text;
using System.Text.RegularExpressions;
using RenoDXLauncher.Models;

namespace RenoDXLauncher.Services;

/// <summary>Matches installed games against catalog entries by normalized name.</summary>
public static partial class MatchService
{
    [GeneratedRegex(@"\b(the|a|an|of|and)\b")]
    private static partial Regex StopWordsRegex();

    /// <summary>Aggressive normalization: lowercase, strip ®™© and diacritics, & → and,
    /// roman numeral–safe, drop all non-alphanumerics.</summary>
    public static string Normalize(string name)
    {
        var s = name.ToLowerInvariant();
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

    /// <summary>Best catalog match for an installed game, or null.
    /// Dedicated mods win over generic engine entries when both match.</summary>
    public static CatalogEntry? FindMatch(GameInfo game, IReadOnlyList<CatalogEntry> catalog)
    {
        var n = Normalize(game.Name);
        if (n.Length == 0) return null;

        var exact = catalog.Where(e => e.NormalizedName == n).ToList();
        if (exact.Count > 0) return PickBest(exact);

        // Tolerate edition suffixes on the installed side only (e.g. "Game — Deluxe Edition").
        var prefix = catalog
            .Where(e => e.NormalizedName.Length >= 6 && n.StartsWith(e.NormalizedName))
            .OrderByDescending(e => e.NormalizedName.Length)
            .ToList();
        if (prefix.Count > 0 && prefix[0].NormalizedName.Length >= n.Length * 6 / 10)
            return PickBest(prefix.Where(e => e.NormalizedName.Length == prefix[0].NormalizedName.Length).ToList());

        return null;
    }

    private static CatalogEntry PickBest(List<CatalogEntry> candidates) =>
        candidates.OrderBy(e => e.Kind == ModKind.Dedicated ? 0 : 1)
                  .ThenByDescending(e => e.Working)
                  .ThenByDescending(e => e.DownloadUrl != null)
                  .First();
}
