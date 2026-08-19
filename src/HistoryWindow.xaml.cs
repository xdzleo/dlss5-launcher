using System.Diagnostics;
using System.Windows;
using RenoDXLauncher.Localization;
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
            ? L.T("History_Subtitle", entry.Slug, entry.Maintainer ?? owner, $"{owner}/{name}")
            : L.T("History_Subtitle_NoRepo", entry.Slug);
        Loaded += async (_, _) => await LoadAsync();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private async Task LoadAsync()
    {
        FooterText.Text = L.T("History_Loading");
        var revisions = await ModHistoryService.GetAsync(_entry);
        List.ItemsSource = revisions;

        if (revisions.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyText.Text = ModHistoryService.WebUrl(_entry) is null
                ? L.T("History_Empty_NoRepo")
                : L.T("History_Empty_RateLimited");
            FooterText.Text = "";
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        var newest = revisions[0].Date.ToLocalTime();
        var date = newest.ToString("dd/MM/yyyy");
        FooterText.Text = revisions.Count == 1
            ? L.T("History_Footer_One", date)
            : L.T("History_Footer", revisions.Count, date);
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
