using System.IO;
using System.Text.RegularExpressions;

namespace RenoDXLauncher.Services;

public enum LoadResult { NoLog, NotLoaded, Loaded, Failed, LimitedBuild, NoAddonSupport }

/// <summary>What ReShade.log says about the last time the game ran.</summary>
public record LoadReport(
    LoadResult Result,
    string? AddonName,
    string? AddonVersion,
    string? Detail,
    DateTime? LastRun)
{
    /// <summary>Short PT-BR verdict shown in the detail panel.</summary>
    public string Message => Result switch
    {
        LoadResult.Loaded => $"Confirmado: o mod carregou no jogo{(AddonName is null ? "" : $" ({AddonName}{(AddonVersion is null ? "" : " v" + AddonVersion)})")}.",
        LoadResult.Failed => $"Falhou: o ReShade tentou carregar o mod e FALHOU: {Detail}",
        LoadResult.LimitedBuild => "Atenção: este ReShade é a build SEM suporte a add-ons — clique em \"Instalar / Atualizar mod\" que eu troco pela versão certa.",
        LoadResult.NoAddonSupport => "Atenção: o ReShade instalado aqui NÃO tem suporte a add-ons (o jogo rodou e ele nem procurou por mods). "
            + "Clique em \"Instalar / Atualizar mod\" que eu substituo pela build com suporte a add-ons.",
        LoadResult.NotLoaded => "Atenção: o jogo rodou com ReShade, mas o mod RenoDX não foi carregado. Confira se o addon está ativado e na pasta certa.",
        _ => "Ainda não há registro: abra o jogo uma vez para eu verificar se o mod carregou.",
    };
}

/// <summary>
/// Reads ReShade.log next to the game exe to VERIFY the mod actually loaded — the only
/// ground truth available without being inside the game. Log strings come from ReShade's
/// addon_manager.cpp ("Loading add-on from '...'", "Registered add-on \"X\" vN...",
/// "Failed to load add-on from '...'", "Skipped loading add-on ... limited add-on functionality").
/// </summary>
public static partial class ReShadeLogService
{
    [GeneratedRegex(@"Registered add-on ""([^""]+)"" v([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegisteredRegex();

    [GeneratedRegex(@"Failed to load add-on from '([^']*renodx[^']*)'[^\r\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex FailedRegex();

    [GeneratedRegex(@"limited add-on functionality", RegexOptions.IgnoreCase)]
    private static partial Regex LimitedRegex();

    [GeneratedRegex(@"Loading add-on from '([^']*renodx[^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex LoadingRegex();

    [GeneratedRegex(@"Searching for add-ons", RegexOptions.IgnoreCase)]
    private static partial Regex SearchingRegex();

    public static LoadReport Check(string targetDir)
    {
        try
        {
            var logPath = Path.Combine(targetDir, "ReShade.log");
            if (!File.Exists(logPath))
                return new LoadReport(LoadResult.NoLog, null, null, null, null);

            var lastRun = File.GetLastWriteTime(logPath);
            string text;
            // the game may still hold the log open — read with full sharing
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();

            if (LimitedRegex().IsMatch(text))
                return new LoadReport(LoadResult.LimitedBuild, null, null, null, lastRun);

            if (FailedRegex().Match(text) is { Success: true } fail)
                return new LoadReport(LoadResult.Failed, null, null, fail.Value.Trim(), lastRun);

            // a renodx addon actually registering itself is the definitive success signal
            foreach (Match m in RegisteredRegex().Matches(text))
            {
                var name = m.Groups[1].Value;
                if (name.Contains("renodx", StringComparison.OrdinalIgnoreCase)
                    || LoadingRegex().IsMatch(text))
                    return new LoadReport(LoadResult.Loaded, name, m.Groups[2].Value, null, lastRun);
            }

            // ReShade rodou (log tem conteúdo) mas NUNCA procurou add-ons: é a build normal
            // (sem suporte a add-on), na qual o .addon64 fica inerte para sempre
            if (!SearchingRegex().IsMatch(text) && text.Length > 512)
                return new LoadReport(LoadResult.NoAddonSupport, null, null, null, lastRun);

            return new LoadReport(LoadResult.NotLoaded, null, null, null, lastRun);
        }
        catch (Exception ex)
        {
            Log.Warn($"ReShade.log check {targetDir}: {ex.Message}");
            return new LoadReport(LoadResult.NoLog, null, null, null, null);
        }
    }
}
