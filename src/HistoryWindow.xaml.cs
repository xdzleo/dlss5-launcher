using System.Diagnostics;
using System.Windows;
using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

namespace RenoDXLauncher;

/// <summary>
/// Version history of a game's mod: every published change to its folder in the maintainer's
/// repo, with date and author.
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly CatalogEntry _entry;

    public HistoryWindow(CatalogEntry entry, string gameName)
    {
        _entry = entry;
        InitializeComponent();
        TitleText.Text = gameName;
        var repo = ModHistoryService.RepoOf(entry);
        SubtitleText.Text = repo is var (owner, name)
            ? $"Mod {entry.Slug} · mantido por {entry.Maintainer ?? owner} em {owner}/{name}"
            : $"Mod {entry.Slug}";
        Loaded += async (_, _) => await LoadAsync();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private async Task LoadAsync()
    {
        FooterText.Text = "Carregando histórico...";
        var revisions = await ModHistoryService.GetAsync(_entry);
        List.ItemsSource = revisions;

        if (revisions.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyText.Text = ModHistoryService.WebUrl(_entry) is null
                ? "Este mod não é publicado por um repositório que eu saiba consultar (provavelmente só Nexus/Discord)."
                : "Não consegui carregar agora — o GitHub limita consultas sem login a 60 por hora. "
                  + "Tente de novo mais tarde ou use “Ver no GitHub”.";
            FooterText.Text = "";
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        var newest = revisions[0].Date.ToLocalTime();
        FooterText.Text = $"{revisions.Count} alterações · última em {newest:dd/MM/yyyy}";
    }

    private void OnOpenWeb(object sender, RoutedEventArgs e)
    {
        var url = ModHistoryService.WebUrl(_entry) ?? _entry.NexusUrl ?? _entry.InfoUrl;
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"open history url: {ex.Message}"); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
