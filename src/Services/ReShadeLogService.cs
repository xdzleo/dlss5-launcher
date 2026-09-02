using System.IO;
using System.Text.RegularExpressions;
using RenoDXLauncher.Localization;

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
    /// <summary>Short verdict shown in the detail panel, in the language currently selected.
    /// Computed on every read, so switching language re-renders it without a reload.</summary>
    public string Message => Result switch
    {
        // the addon id stays verbatim: it is the name ReShade itself logged
        LoadResult.Loaded => AddonName is null
            ? L.T("Install_Verify_Loaded")
            : L.T("Install_Verify_Loaded_Addon",
                AddonVersion is null ? AddonName : $"{AddonName} v{AddonVersion}"),
        LoadResult.Failed => L.T("Install_Verify_Failed", Detail),
        LoadResult.LimitedBuild => L.T("Install_Verify_LimitedBuild"),
        LoadResult.NoAddonSupport => L.T("Install_Verify_NoAddonSupport"),
        LoadResult.NotLoaded => L.T("Install_Verify_NotLoaded"),
        _ => L.T("Install_Verify_NoLog"),
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

    // every add-on load, renodx or not, so a "Registered" line can be tied to the FILE that
    // registered it; the built-in batch counts as a (non-renodx) source of its own
    [GeneratedRegex(@"Loading (?:built-in add-ons|add-on from '([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex AnyLoadingRegex();

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

            // A renodx addon actually registering itself is the definitive success signal.
            //
            // Um "Registered add-on" so prova algo sobre o RenoDX se foi o RenoDX que registrou.
            // O ReShade escreve "Loading add-on from '<arquivo>'" e, de dentro do DllMain desse
            // arquivo, o "Registered add-on" — a linha de Loading imediatamente anterior diz de
            // que arquivo veio o registro. A versao antiga aceitava o primeiro registro de
            // QUALQUER addon assim que houvesse alguma linha de Loading do renodx no log, e o
            // REST (que vem antes na ordem alfabetica) passava por "mod carregado", com o nome
            // dele no painel — inclusive quando o renodx entrou e nunca se registrou.
            var loads = AnyLoadingRegex().Matches(text);
            LoadReport? companion = null;
            foreach (Match m in RegisteredRegex().Matches(text))
            {
                var name = m.Groups[1].Value;
                var source = loads.LastOrDefault(l => l.Index < m.Index)?.Groups[1].Value ?? "";
                if (!name.Contains("renodx", StringComparison.OrdinalIgnoreCase)
                    && !source.Contains("renodx", StringComparison.OrdinalIgnoreCase))
                    continue;
                var report = new LoadReport(LoadResult.Loaded, name, m.Groups[2].Value, null, lastRun);
                // o veredito e sobre o mod DO JOGO; um companion (neural, dlss5) so responde
                // por ele quando nao ha mod do jogo no log
                if (AddonService.IsCompanionAddon(Path.GetFileName(source))) companion ??= report;
                else return report;
            }

            // o DLL do mod entrou (Loading) mas nunca se registrou: o ReShade nao chama isso
            // de falha, mas para quem joga e — o mod nao esta rodando
            var silent = LoadingRegex().Matches(text)
                .FirstOrDefault(l => !AddonService.IsCompanionAddon(Path.GetFileName(l.Groups[1].Value)));
            if (silent != null)
                return new LoadReport(LoadResult.Failed, null, null, silent.Value.Trim(), lastRun);
            if (companion != null) return companion;

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
