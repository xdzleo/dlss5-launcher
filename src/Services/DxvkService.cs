using System.IO;
using System.Net.Http;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// DXVK: a segunda rota para jogo Direct3D 9, e a que o dgVoodoo2 nao cobre.
///
/// O caminho DX9 -> DLSS 5 sempre precisou de um tradutor, porque o ReShade em D3D9 para no
/// Shader Model 3 e nenhum provedor de motion vectors compila. O dgVoodoo2 resolvia isso
/// entregando D3D11 — quando funciona. Em jogo que ele derruba (Resident Evil Revelations 2 e
/// Bayonetta, os dois confirmados por bisseccao nesta maquina, com o MESMO binario que roda
/// Saints Row 2 e Bully sem queixa) nao havia rota nenhuma.
///
/// O DXVK traduz D3D9 para Vulkan em vez de D3D11, e o Revelations 2 roda com ele sem crash.
/// Isso muda o resto da cadeia: o ReShade entra como CAMADA Vulkan (nao como proxy d3d9.dll),
/// e o add-on precisa falar Vulkan — que e exatamente o que o addon32 com transporte Vulkan faz.
///
/// A escolha entre os dois nao e preferencia estetica: o dgVoodoo continua sendo o padrao,
/// porque e a rota testada em mais jogos. O DXVK entra onde ele falha.
/// </summary>
public static class DxvkService
{
    private const string Repo = "doitsujin/dxvk";
    public const string D3d9File = "d3d9.dll";

    public static string LibraryDir { get; } = Path.Combine(AppPaths.DataDir, "dxvk");
    private static string LibraryD3d9_32 { get; } = Path.Combine(LibraryDir, "x32", D3d9File);

    /// <summary>Só github.com e seus domínios de download, como nas outras buscas do launcher.</summary>
    private static readonly string[] AllowedHosts =
        { "github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    private static bool HostOk(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RenoDXLauncher/1.0");
        return http;
    }

    /// <summary>O DXVK de 32 bits ja esta na biblioteca?</summary>
    public static bool InLibrary => File.Exists(LibraryD3d9_32);

    /// <summary>
    /// Jogos em que o DXVK foi testado e PERDEU para o dgVoodoo2.
    ///
    /// Os dois tradutores nao se ordenam: cada um cobre um conjunto, e os conjuntos nao se
    /// contem. Medido nesta maquina, com o mesmo add-on e o mesmo runtime:
    ///
    ///   Resident Evil Revelations 2  dgVoodoo crasha (0xc0000005 no d3d9.dll dele)
    ///                                DXVK roda, 1800 frames avaliados, 64 fps
    ///   Saints Row 2                 DXVK crasha (0xc0000005 no d3d9.dll dele) aos ~25 s,
    ///                                DEPOIS de o DLSS ja estar avaliando — o jogo sobe, o
    ///                                feed entrega 600 frames, e entao morre
    ///                                dgVoodoo roda estavel
    ///
    /// O padrao e o DXVK, porque cobre mais jogos e e mantido ativamente. Esta lista existe
    /// para os casos ja verificados em que ele perde — e so entra aqui o que foi testado
    /// dentro do jogo, nunca por suposicao.
    /// </summary>
    private static readonly string[] PreferemDgVoodoo =
    {
        "sr2_pc.exe",   // Saints Row 2
    };

    /// <summary>O DXVK e a rota recomendada para este executavel?</summary>
    public static bool RecomendadoPara(string? exePath)
    {
        if (exePath is null) return true;
        var nome = Path.GetFileName(exePath);
        return !PreferemDgVoodoo.Contains(nome, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Este jogo esta rodando pela rota DXVK? (o d3d9.dll dele e o do DXVK)</summary>
    public static bool IsDeployed(string targetDir)
    {
        var dll = Path.Combine(targetDir, D3d9File);
        if (!File.Exists(dll)) return false;
        // O d3d9.dll do DXVK passa de 5 MB; o do dgVoodoo fica em ~500 KB, e o do Windows nem
        // aparece na pasta do jogo. O tamanho separa os tres sem precisar abrir o PE.
        try { return new FileInfo(dll).Length > 3 * 1024 * 1024; }
        catch { return false; }
    }

    /// <summary>Baixa o DXVK para a biblioteca. Sem efeito se ja estiver la.</summary>
    public static async Task FetchAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (InLibrary) return;
        Directory.CreateDirectory(LibraryDir);

        progress?.Report(L.T("Dxvk_Fetching"));
        using var http = NewClient();
        var api = $"https://api.github.com/repos/{Repo}/releases/latest";
        var json = await http.GetStringAsync(api, ct);

        // O asset e um .tar.gz — o formato que o projeto publica.
        var url = System.Text.RegularExpressions.Regex
            .Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+dxvk-[0-9.]+\\.tar\\.gz)\"")
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault(HostOk);
        if (url is null) throw new InvalidOperationException(L.T("Dxvk_NoAsset"));

        var tgz = Path.Combine(LibraryDir, "dxvk.tar.gz");
        await using (var s = await http.GetStreamAsync(url, ct))
        await using (var f = File.Create(tgz))
            await s.CopyToAsync(f, ct);

        // tar nativo do Windows 10+ resolve .tar.gz sem dependencia externa.
        var tmp = Path.Combine(LibraryDir, "unpack");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        var psi = new System.Diagnostics.ProcessStartInfo("tar", $"-xzf \"{tgz}\" -C \"{tmp}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using (var p = System.Diagnostics.Process.Start(psi)) { if (p is not null) await p.WaitForExitAsync(ct); }

        // Guarda so o que interessa: o d3d9.dll de 32 bits (e o de 64, para jogo Vulkan x64).
        foreach (var (arch, dest) in new[] { ("x32", Path.Combine(LibraryDir, "x32")),
                                             ("x64", Path.Combine(LibraryDir, "x64")) })
        {
            var found = Directory.EnumerateFiles(tmp, D3d9File, SearchOption.AllDirectories)
                                 .FirstOrDefault(p => p.Replace('/', '\\').Contains($"\\{arch}\\"));
            if (found is null) continue;
            Directory.CreateDirectory(dest);
            File.Copy(found, Path.Combine(dest, D3d9File), overwrite: true);
        }
        try { Directory.Delete(tmp, true); File.Delete(tgz); } catch { }

        if (!InLibrary) throw new InvalidOperationException(L.T("Dxvk_NoAsset"));
        Log.Info($"dxvk: baixado para a biblioteca ({new FileInfo(LibraryD3d9_32).Length:N0} bytes)");
    }

    /// <summary>
    /// Poe o d3d9.dll do DXVK na pasta do jogo, guardando o que estiver la.
    ///
    /// O dgVoodoo sai de cena junto: os dois disputam o mesmo nome de arquivo, e deixar os
    /// dois na pasta e como nao ter nenhum.
    /// </summary>
    public static void Deploy(string targetDir, IProgress<string>? progress = null)
    {
        if (!InLibrary) throw new InvalidOperationException(L.T("Dxvk_NotInLibrary"));

        // dgVoodoo fora: mesmo nome, e quem chega por ultimo venceria por acidente.
        foreach (var n in new[] { "D3D9.dll", "d3d9.dll" })
        {
            var p = Path.Combine(targetDir, n);
            if (File.Exists(p) && !IsDeployed(targetDir))
            {
                var bak = p + ".pre-dxvk";
                if (!File.Exists(bak)) File.Move(p, bak);
                else File.Delete(p);
                progress?.Report(L.T("Dxvk_ReplacedD3d9"));
                break;
            }
        }
        foreach (var n in new[] { "dgVoodoo.conf", "dgVoodooCpl.exe" })
        {
            var p = Path.Combine(targetDir, n);
            if (File.Exists(p)) { try { File.Move(p, p + ".pre-dxvk", overwrite: true); } catch { } }
        }

        File.Copy(LibraryD3d9_32, Path.Combine(targetDir, D3d9File), overwrite: true);
        progress?.Report(L.T("Dxvk_Deployed"));
        Log.Info($"dxvk: d3d9.dll implantado em {targetDir}");
    }

    /// <summary>Tira o DXVK e devolve o que estava no lugar.</summary>
    public static void Remove(string targetDir)
    {
        var dll = Path.Combine(targetDir, D3d9File);
        if (IsDeployed(targetDir)) { try { File.Delete(dll); } catch { } }
        foreach (var n in new[] { "D3D9.dll", "d3d9.dll", "dgVoodoo.conf", "dgVoodooCpl.exe" })
        {
            var bak = Path.Combine(targetDir, n + ".pre-dxvk");
            if (File.Exists(bak)) { try { File.Move(bak, Path.Combine(targetDir, n), overwrite: true); } catch { } }
        }
    }
}
