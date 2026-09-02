using RenoDXLauncher.Localization;
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
    /// <summary>O valor que a linha mostra quando a chave nao esta no ini. E contra ELE que
    /// IsDirty compara nesse caso: o manifesto tem dezenas de ajustes sem default e com min
    /// diferente de zero, e comparar contra `Default ?? 0` fazia essas linhas nascerem sujas
    /// — o Salvar gravava no preset chaves que o usuario nunca tocou, com o valor do min.</summary>
    private readonly double _fallback;

    public SettingVm(SettingsService.SettingValue sv)
    {
        Def = sv.Def;
        _originalValue = sv.Current;
        _fallback = sv.Def.Default ?? sv.Def.Min ?? 0;
        _value = sv.Current ?? _fallback;

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
    public string SectionName => Def.Section ?? L.T("Settings_Section_Other");

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
        ? Math.Abs(_value - _fallback) > 0.0001
        : Math.Abs(_value - _originalValue.Value) > 0.0001;

    public bool WasSetInIni => _originalValue != null;

    /// <summary>Translated label for the settings every mod shares; passthrough for the rest.
    /// The label the manifest carries is data written by the mod author, always in English —
    /// it is the lookup key here, never screen text, so it must NOT be localized.</summary>
    private static string Translate(string label) =>
        LabelKey(label) is { } resKey ? L.T(resKey) : label;

    private static string? LabelKey(string label) => label switch
    {
        "Peak Brightness" => "Settings_PeakBrightness_Label",
        "Game Brightness" => "Settings_GameBrightness_Label",
        "UI Brightness" => "Settings_UiBrightness_Label",
        "Tone Mapper" => "Settings_ToneMapper_Label",
        "Gamma Correction" => "Settings_GammaCorrection_Label",
        "Hue Correction" => "Settings_HueCorrection_Label",
        "Hue Processor" => "Settings_HueProcessor_Label",
        "Exposure" => "Settings_Exposure_Label",
        "Highlights" => "Settings_Highlights_Label",
        "Shadows" => "Settings_Shadows_Label",
        "Contrast" => "Settings_Contrast_Label",
        "Saturation" => "Settings_Saturation_Label",
        "Highlight Saturation" => "Settings_HighlightSaturation_Label",
        "Blowout" => "Settings_Blowout_Label",
        "Flare" => "Settings_Flare_Label",
        "LUT Strength" => "Settings_LutStrength_Label",
        "Color Grade Strength" => "Settings_ColorGradeStrength_Label",
        "Scene Grade Strength" => "Settings_SceneGradeStrength_Label",
        "Bloom" => "Settings_Bloom_Label",
        "Vignette" => "Settings_Vignette_Label",
        "Film Grain" => "Settings_FilmGrain_Label",
        "Settings Mode" => "Settings_SettingsMode_Label",
        _ => null,
    };

    private static string? TooltipFor(SettingDef def)
    {
        // Casa pela chave do ini (dado do mod, nunca traduzida). Quando o launcher tem texto
        // proprio para o ajuste, ele ganha do tooltip do manifesto: e escrito para quem esta
        // calibrando pela primeira vez, e o do manifesto pressupoe o overlay aberto.
        var resKey = def.Key.ToLowerInvariant() switch
        {
            "tonemappeaknits" => "Settings_PeakBrightness_Tooltip",
            "tonemapgamenits" => "Settings_GameBrightness_Tooltip",
            "tonemapuinits" => "Settings_UiBrightness_Tooltip",
            "tonemaptype" => "Settings_ToneMapper_Tooltip",
            "tonemapgammacorrection" or "gammacorrection" => "Settings_GammaCorrection_Tooltip",
            _ => null,
        };
        return resKey != null ? L.T(resKey) : def.Tooltip;
    }
}
