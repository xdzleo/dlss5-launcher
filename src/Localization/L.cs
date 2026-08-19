using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace RenoDXLauncher.Localization;

/// <summary>
/// Acesso a texto traduzido. Uma instancia unica, observavel, para que trocar de idioma
/// atualize a janela aberta em vez de exigir reinicio.
///
/// Sem biblioteca de terceiro de proposito: o projeto nao tem nenhum PackageReference, e
/// cada dependencia nova e superficie nova para heuristica de antivirus e para cadeia de
/// suprimentos. ResourceManager e do proprio .NET e resolve o caso inteiro.
///
/// Adicionar um idioma = adicionar um Strings.&lt;tag&gt;.resx e uma linha em Available.
/// Nada mais no codigo precisa mudar.
/// </summary>
public sealed class L : INotifyPropertyChanged
{
    public static L Instance { get; } = new();

    private static readonly ResourceManager Rm =
        new("RenoDXLauncher.Localization.Strings", typeof(L).Assembly);

    private L() { }

    /// <summary>Idiomas com traducao completa. A chave e a tag BCP-47.</summary>
    public static readonly (string Tag, string NativeName)[] Available =
    {
        ("pt-BR", "Português (Brasil)"),
        ("en",    "English"),
    };

    private static CultureInfo _culture = CultureInfo.CurrentUICulture;

    /// <summary>Cultura em uso. Padrao: a do Windows, com queda para o resx neutro
    /// (portugues) quando o idioma do sistema nao tem traducao.</summary>
    public static CultureInfo Culture => _culture;

    /// <summary>
    /// Define o idioma. <c>null</c> ou vazio volta para o idioma do Windows.
    /// Dispara a notificacao que faz a interface aberta se redesenhar.
    /// </summary>
    public static void SetLanguage(string? bcp47Tag)
    {
        var novo = string.IsNullOrWhiteSpace(bcp47Tag)
            ? CultureInfo.CurrentUICulture
            : SafeCulture(bcp47Tag!);

        if (novo.Name == _culture.Name) return;
        _culture = novo;
        // O indexador e a unica propriedade que existe; string.Empty avisa que TODAS as
        // chaves mudaram, que e exatamente o caso numa troca de idioma.
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(string.Empty));
    }

    private static CultureInfo SafeCulture(string tag)
    {
        try { return CultureInfo.GetCultureInfo(tag); }
        catch (CultureNotFoundException) { return CultureInfo.CurrentUICulture; }
    }

    /// <summary>Indexador usado pelo binding do XAML: <c>{loc:Tr Alguma_Chave}</c>.</summary>
    public string this[string key] => Get(key);

    /// <summary>Texto da chave. Se a chave nao existir, devolve a propria chave — some
    /// da tela como texto estranho, mas nao derruba a janela, e fica obvio no screenshot
    /// de quem reportar.</summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        try { return Rm.GetString(key, _culture) ?? key; }
        catch (MissingManifestResourceException) { return key; }
    }

    /// <summary>Texto da chave com <see cref="string.Format(IFormatProvider,string,object[])"/>.
    /// Formata na cultura em uso, para numero e data sairem no formato do idioma.</summary>
    public static string T(string key, params object?[] args)
    {
        var s = Get(key);
        if (args.Length == 0) return s;
        try { return string.Format(_culture, s, args); }
        catch (FormatException) { return s; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
