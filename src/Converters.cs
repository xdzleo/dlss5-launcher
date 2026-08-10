using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RenoDXLauncher.Services;

namespace RenoDXLauncher;

/// <summary>GameStore → the store's official brand mark (vector geometry from Icons.xaml).</summary>
public class StoreIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is RenoDXLauncher.Models.GameStore s ? s switch
        {
            RenoDXLauncher.Models.GameStore.Steam => "IconSteam",
            RenoDXLauncher.Models.GameStore.Epic => "IconEpic",
            RenoDXLauncher.Models.GameStore.Gog => "IconGog",
            RenoDXLauncher.Models.GameStore.Xbox => "IconXbox",
            RenoDXLauncher.Models.GameStore.EA => "IconEA",
            RenoDXLauncher.Models.GameStore.BattleNet => "IconBattleNet",
            RenoDXLauncher.Models.GameStore.Rockstar => "IconRockstar",
            RenoDXLauncher.Models.GameStore.Ubisoft => "IconUbisoft",
            _ => "IconFolder",
        } : "IconFolder";
        return System.Windows.Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>int count → Visible when &gt; 0, else Collapsed.</summary>
public class CountToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Background/foreground tint for a recommendation card, by kind.</summary>
public class AdviceColorConverter : IValueConverter
{
    public bool Foreground { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // (background, text) per advice kind
        var (bg, fg) = value is AdviceKind k ? k switch
        {
            AdviceKind.HdrOff => ("#3A1E1E", "#FFB4A8"),      // red — turn game HDR off
            AdviceKind.HdrOn => ("#193322", "#A8E8C0"),        // green — turn game HDR on
            AdviceKind.Deprecated => ("#3A1E1E", "#FFB4A8"),   // red
            AdviceKind.AntiCheat => ("#3A3018", "#F0D28A"),    // amber
            AdviceKind.Renderer => ("#1E2A3A", "#A8CCF0"),     // blue
            _ => ("#26262D", "#ECECF1"),
        } : ("#26262D", "#ECECF1");
        var hex = Foreground ? fg : bg;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>File path → BitmapImage decoded at tile size (not the full 600x900), loaded
/// eagerly and frozen so the cache file isn't kept locked and the working set stays small.</summary>
public class CoverImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.DecodePixelWidth = 336;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
