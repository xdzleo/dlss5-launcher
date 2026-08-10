using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RenoDXLauncher;

public enum DialogKind { Info, Warning, Danger, Question }

/// <summary>
/// Themed replacement for MessageBox — the system dialog ignores the app's dark theme and
/// looks out of place. Same call shape: static helpers returning the pressed button.
/// </summary>
public partial class DialogWindow : Window
{
    private MessageBoxResult _result = MessageBoxResult.Cancel;

    private DialogWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _result = MessageBoxResult.Cancel; Close(); }
            if (e.Key == Key.Enter) { _result = PrimaryResult; Close(); }
        };
    }

    private MessageBoxResult PrimaryResult { get; set; } = MessageBoxResult.OK;
    private MessageBoxResult SecondResult { get; set; } = MessageBoxResult.Cancel;
    private MessageBoxResult ThirdResult { get; set; } = MessageBoxResult.Cancel;

    private void OnPrimary(object s, RoutedEventArgs e) { _result = PrimaryResult; Close(); }
    private void OnSecond(object s, RoutedEventArgs e) { _result = SecondResult; Close(); }
    private void OnThird(object s, RoutedEventArgs e) { _result = ThirdResult; Close(); }

    private void ApplyKind(DialogKind kind)
    {
        var (bg, fg, icon) = kind switch
        {
            DialogKind.Danger => ("#3A1E1E", "#FF9E9E", "IconBlock"),
            DialogKind.Warning => ("#2A2415", "#F0B429", "IconWarning"),
            DialogKind.Question => ("#1D2A3A", "#7FB2F0", "IconInfo"),
            _ => ("#1D2A3A", "#7FB2F0", "IconInfo"),
        };
        IconBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        IconGlyph.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        if (Application.Current.TryFindResource(icon) is Geometry g) IconGlyph.Data = g;
    }

    /// <summary>OK-only notice.</summary>
    public static void Show(Window? owner, string title, string body, DialogKind kind = DialogKind.Info)
    {
        var w = Build(owner, title, body, kind);
        w.SecondButton.Visibility = Visibility.Collapsed;
        w.PrimaryButton.Content = "Entendi";
        w.PrimaryResult = MessageBoxResult.OK;
        w.ShowDialog();
    }

    /// <summary>Confirm / cancel. Returns true when the user confirms.</summary>
    public static bool Confirm(Window? owner, string title, string body, string confirmText = "Continuar",
        DialogKind kind = DialogKind.Question)
    {
        var w = Build(owner, title, body, kind);
        w.SecondButton.Content = "Cancelar";
        w.SecondResult = MessageBoxResult.Cancel;
        w.PrimaryButton.Content = confirmText;
        w.PrimaryResult = MessageBoxResult.Yes;
        w.ShowDialog();
        return w._result == MessageBoxResult.Yes;
    }

    /// <summary>Three-way choice (Yes / No / Cancel), mirroring MessageBoxResult.</summary>
    public static MessageBoxResult Choose(Window? owner, string title, string body,
        string yesText, string noText, DialogKind kind = DialogKind.Question)
    {
        var w = Build(owner, title, body, kind);
        w.ThirdButton.Visibility = Visibility.Visible;
        w.ThirdButton.Content = "Cancelar";
        w.ThirdResult = MessageBoxResult.Cancel;
        w.SecondButton.Content = noText;
        w.SecondResult = MessageBoxResult.No;
        w.PrimaryButton.Content = yesText;
        w.PrimaryResult = MessageBoxResult.Yes;
        w.ShowDialog();
        return w._result;
    }

    private static DialogWindow Build(Window? owner, string title, string body, DialogKind kind)
    {
        var w = new DialogWindow { Title = title };
        if (owner != null && owner.IsVisible) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.TitleText.Text = title;
        w.BodyText.Text = body;
        w.ApplyKind(kind);
        return w;
    }
}
