using System.Windows;
using RenoDXLauncher.Localization;
using RenoDXLauncher.ViewModels;

namespace RenoDXLauncher;

public partial class ProfileWindow : Window
{
    private readonly MainViewModel _vm;

    public ProfileWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        PeakSlider.Value = vm.Config.PeakNits;
        GameSlider.Value = vm.Config.GameNits;
        UiSlider.Value = vm.Config.UiNits;
        ApplyOnInstall.IsChecked = vm.Config.ApplyProfileOnInstall;
        PopulateLanguages();
        UpdateTexts();
    }

    /// <summary>Item do seletor de idioma. Tag vazia = seguir o Windows.</summary>
    /// <summary>Item do seletor. Publico de proposito: DisplayMemberPath e SelectedValuePath
    /// resolvem por reflexao, e WPF falha em silencio com tipo nao-publico — o combo mostraria
    /// o nome do tipo em vez do idioma.</summary>
    public sealed record LanguageChoice(string Tag, string Nome);

    private bool _languagesReady;

    private void PopulateLanguages()
    {
        var itens = new List<LanguageChoice> { new("", L.T("Settings_Language_System")) };
        foreach (var (tag, nativo) in L.Available)
            itens.Add(new LanguageChoice(tag, nativo));

        LanguageBox.ItemsSource = itens;
        LanguageBox.SelectedValue = _vm.Config.Language ?? "";
        // so depois de preencher: o SelectionChanged dispara durante a montagem, e trocar
        // o idioma ali reescreveria a config antes de a janela existir.
        _languagesReady = true;
    }

    private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_languagesReady) return;
        if (LanguageBox.SelectedValue is not string tag) return;

        var escolhido = string.IsNullOrEmpty(tag) ? null : tag;
        if (escolhido == _vm.Config.Language) return;

        _vm.Config.Language = escolhido;
        _vm.Config.Save();
        // A interface toda esta ligada por binding ao indexador de L, entao a troca
        // aparece na hora, sem fechar a janela.
        L.SetLanguage(escolhido);
        UpdateTexts();
    }

    private void OnAnyValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateTexts();

    private void UpdateTexts()
    {
        if (PeakText is null) return; // during InitializeComponent
        PeakText.Text = L.T("Common_NitsValue", PeakSlider.Value.ToString("0"));
        GameText.Text = L.T("Common_NitsValue", GameSlider.Value.ToString("0"));
        UiText.Text = L.T("Common_NitsValue", UiSlider.Value.ToString("0"));
    }

    private void OnPeakPreset(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && double.TryParse(b.Content as string, out var v))
            PeakSlider.Value = v;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.SaveDisplayProfile(PeakSlider.Value, GameSlider.Value, UiSlider.Value, ApplyOnInstall.IsChecked == true);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
