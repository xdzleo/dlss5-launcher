using System.Windows.Data;
using System.Windows.Markup;

namespace RenoDXLauncher.Localization;

/// <summary>
/// Extensao de marcacao do XAML: <c>Text="{loc:Tr MainWindow_Titulo}"</c>.
///
/// Devolve um Binding, e nao a string pronta, de proposito: e o binding que faz a janela
/// ja aberta se redesenhar quando o idioma muda. Com valor literal, trocar de idioma
/// exigiria fechar e reabrir a janela.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    /// <summary>Chave no Strings.resx.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = L.Instance,
            Mode   = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
