using RenoDXLauncher.Models;
using RenoDXLauncher.Services;

namespace RenoDXLauncher.ViewModels;

/// <summary>One editable RenoDX setting row (slider / combo / checkbox).</summary>
public class SettingVm : ObservableObject
{
    public SettingDef Def { get; }
    private readonly double? _originalValue;
    private double _value;
    private readonly double _min, _max, _step;

    public SettingVm(SettingsService.SettingValue sv)
    {
        Def = sv.Def;
        _originalValue = sv.Current;
        _value = sv.Current ?? sv.Def.Default ?? sv.Def.Min ?? 0;

        // A faixa TEM que conter o default, o valor do ini e o valor atual. Um slider cujo
        // teto exclui o próprio default faz o WPF coagir o valor (1000 nits -> 100) e o app
        // grava esse lixo no ReShade.ini — o oposto do propósito do programa.
        var mustFit = new[] { _value, Def.Default ?? _value, _originalValue ?? _value };
        _min = Math.Min(Def.Min ?? 0, mustFit.Min());
        _max = Def.Max ?? FallbackMax(mustFit.Max());
        if (_max <= _min) _max = _min + 1;
        _step = Def.Type == "float" && _max <= 2 ? 0.01 : 1;
    }

    /// <summary>Teto quando o manifesto não traz max: convenção dos mods que TÊM max
    /// (peak 4000, game/UI 500), sempre esticada para caber o maior valor conhecido.</summary>
    private double FallbackMax(double floor) => Def.Key.ToLowerInvariant() switch
    {
        "tonemappeaknits" => Math.Max(4000, floor),
        "tonemapgamenits" or "tonemapuinits" => Math.Max(500, floor),
        _ => Def.Type == "float" && floor <= 2 ? Math.Max(2, floor) : Math.Max(100, floor),
    };

    public string Label => Translate(Def.Label ?? Def.Key);
    public string? Tooltip => TooltipFor(Def);
    public string SectionName => Def.Section ?? "Outros";

    public bool IsCombo => Def.Type is "int" or "bool" && Def.Labels is { Count: > 0 };
    public bool IsCheck => Def.Type == "bool" && !IsCombo;
    public bool IsSlider => !IsCombo && !IsCheck;

    public IReadOnlyList<string>? ComboLabels => Def.Labels;

    public double Min => _min;
    public double Max => _max;
    public double Step => _step;

    public double Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
            {
                OnPropertyChanged(nameof(ValueText));
                OnPropertyChanged(nameof(ComboIndex));
                OnPropertyChanged(nameof(IsChecked));
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public int ComboIndex
    {
        get => (int)Math.Round(_value);
        set => Value = value;
    }

    public bool IsChecked
    {
        get => _value >= 0.5;
        set => Value = value ? 1 : 0;
    }

    public string ValueText => Def.Type == "float" ? _value.ToString("0.##") : ((int)Math.Round(_value)).ToString();

    /// <summary>Value differs from what the ini currently holds (unsaved edit or unset key).</summary>
    public bool IsDirty => _originalValue is null
        ? Math.Abs(_value - (Def.Default ?? 0)) > 0.0001
        : Math.Abs(_value - _originalValue.Value) > 0.0001;

    public bool WasSetInIni => _originalValue != null;

    /// <summary>PT-BR labels for the settings every mod shares; passthrough for the rest.</summary>
    private static string Translate(string label) => label switch
    {
        "Peak Brightness" => "Brilho máximo (nits)",
        "Game Brightness" => "Brilho do jogo (nits)",
        "UI Brightness" => "Brilho da interface (nits)",
        "Tone Mapper" => "Tone mapper",
        "Gamma Correction" => "Correção de gamma",
        "Hue Correction" => "Correção de matiz",
        "Hue Processor" => "Processador de matiz",
        "Exposure" => "Exposição",
        "Highlights" => "Realces",
        "Shadows" => "Sombras",
        "Contrast" => "Contraste",
        "Saturation" => "Saturação",
        "Highlight Saturation" => "Saturação dos realces",
        "Blowout" => "Blowout (dessaturar realces)",
        "Flare" => "Flare / brilho difuso",
        "LUT Strength" => "Intensidade do LUT",
        "Color Grade Strength" => "Intensidade da gradação",
        "Scene Grade Strength" => "Gradação da cena",
        "Bloom" => "Bloom",
        "Vignette" => "Vinheta",
        "Film Grain" => "Granulação de filme",
        "Settings Mode" => "Modo do overlay",
        _ => label,
    };

    private static string? TooltipFor(SettingDef def)
    {
        var key = def.Key.ToLowerInvariant();
        var extra = key switch
        {
            "tonemappeaknits" =>
                "Pico de brilho do SEU monitor em nits — não é gosto. Descubra no app Calibração de HDR do Windows (ponto onde o padrão some) ou na certificação VESA (400/600/1000...). Valores típicos: OLED 800–1000, miniLED 1000–1500.",
            "tonemapgamenits" =>
                "Brilho do \"branco de papel\" (100% branco difuso). Padrão 203 nits (norma ITU BT.2408). Faixa saudável: 100–300 conforme a claridade do ambiente. Nunca acima do brilho máximo.",
            "tonemapuinits" =>
                "Brilho de HUD e menus. Recomendado: 203 (igual ao brilho do jogo). Aumente só se a interface parecer apagada.",
            "tonemaptype" =>
                "Curva de tons. Mantenha o padrão do mod (geralmente RenoDRT: visual original com HDR e preservação de matiz). ACES muda bastante a imagem; None não faz tonemap (pode estourar).",
            "tonemapgammacorrection" or "gammacorrection" =>
                "Corrige sombras lavadas/acinzentadas (jogos SDR masterizados em gamma 2.2). Mantenha o padrão do mod; ligue se o preto parecer elevado.",
            _ => null,
        };
        if (extra != null && def.Tooltip != null) return extra;
        return extra ?? def.Tooltip;
    }
}
