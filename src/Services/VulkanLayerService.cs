using System.IO;
using Microsoft.Win32;
using RenoDXLauncher.Localization;

namespace RenoDXLauncher.Services;

/// <summary>
/// ReShade em jogo Vulkan: nao e um proxy de DLL, e uma CAMADA do loader Vulkan.
///
/// Um jogo Vulkan nao carrega dxgi.dll, d3d11.dll nem nada parecido — ele fala com
/// `vulkan-1.dll` e mais nada. Instalar o ReShade como `dxgi.dll` ali deixa o arquivo na pasta
/// sem que uma linha dele rode: nao ha sequer um ReShade.log para dizer que falhou. Era o que
/// acontecia com o DOOM Eternal (`DOOMEternalx64vk.exe`), que o launcher tratava como um jogo
/// DXGI qualquer.
///
/// O caminho certo e registrar o ReShade como camada implicita, que o loader Vulkan carrega em
/// todo `vkCreateInstance`. Tres detalhes decidem entre funcionar e quebrar, e os tres custaram
/// caro para descobrir:
///
///   1. `library_path` TEM de ser absoluto. Com um caminho relativo o loader nao acha a DLL, e
///      quando uma camada implicita falha a carregar ele NAO a ignora — derruba o
///      `vkCreateInstance` inteiro. O sintoma nao e "o ReShade nao apareceu", e sim TODO
///      aplicativo Vulkan da maquina parar de abrir.
///   2. O loader le apenas HKEY_LOCAL_MACHINE. Registrar em HKCU nao produz efeito nenhum e nao
///      gera erro — a camada simplesmente nao existe para ele.
///   3. Jogo de 32 bits procura em `SOFTWARE\WOW6432Node\...`; o de 64 bits, em `SOFTWARE\...`.
///      Registrar no no errado tem o mesmo silencio do item 2.
/// </summary>
public static class VulkanLayerService
{
    /// <summary>
    /// O nome com que a camada se registra — e ele NAO e "VK_LAYER_reshade".
    ///
    /// O DOOM Eternal carrega uma lista de camadas recusadas dentro do proprio executavel
    /// (`CheckBlacklistedLayers` no log dele), e o ReShade esta nela por nome, junto com
    /// Bandicam, PlayClaw, fpsmon e o overlay da Twitch. Com o nome de fabrica o jogo abre
    /// normalmente e a camada e silenciosamente descartada: nenhum ReShade.log, nenhum aviso,
    /// nada que indique o motivo.
    ///
    /// O bloqueio e por nome e o nome vem deste manifesto, que e nosso. Trocado, o ReShade
    /// carrega e o addon neural registra — verificado no proprio DOOM Eternal.
    /// </summary>
    private const string LayerName = "VK_LAYER_renodx_neural";
    private const string SubDir = "vklayer";

    private const string Key64 = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";
    private const string Key32 = @"SOFTWARE\WOW6432Node\Khronos\Vulkan\ImplicitLayers";

    /// <summary>
    /// Este jogo renderiza em Vulkan?
    ///
    /// A tabela de importacao nao serve: praticamente todo motor carrega `vulkan-1.dll` por
    /// LoadLibrary, para poder cair em outra API quando o driver nao tem Vulkan. Vale a
    /// combinacao — menciona vulkan-1 e NAO importa uma API da Microsoft — mais o costume da
    /// id Software de dizer no nome do arquivo (`DOOMEternalx64vk.exe`).
    /// </summary>
    public static bool Applies(string? exePath)
    {
        if (exePath is null || !File.Exists(exePath)) return false;

        var nome = Path.GetFileNameWithoutExtension(exePath);
        if (nome.EndsWith("vk", StringComparison.OrdinalIgnoreCase)
            || nome.EndsWith("_vulkan", StringComparison.OrdinalIgnoreCase)) return true;

        var pe = PeUtils.Inspect(exePath);
        if (pe is null) return false;

        // Importar uma API da Microsoft decide contra: o jogo tem um caminho D3D e o ReShade
        // engancha por ele, que e mais simples e mais testado do que a camada.
        if (pe.Imports.Any(i => i.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)
                                || i.StartsWith("d3d1", StringComparison.OrdinalIgnoreCase))) return false;

        return ContemTexto(exePath, "vulkan-1.dll");
    }

    /// <summary>A camada ja esta registrada para este jogo?</summary>
    public static bool IsRegistered(string targetDir, bool jogo64Bits)
    {
        try
        {
            var json = ManifestPath(targetDir);
            using var k = Registry.LocalMachine.OpenSubKey(jogo64Bits ? Key64 : Key32);
            return k?.GetValue(json) is not null;
        }
        catch { return false; }
    }

    private static string LayerDir(string targetDir) => Path.Combine(targetDir, SubDir);
    private static string ManifestPath(string targetDir) =>
        Path.Combine(LayerDir(targetDir), "ReShade.json");

    /// <summary>
    /// Poe a DLL e o manifesto na pasta do jogo e registra a camada.
    ///
    /// A DLL vai para uma subpasta propria em vez da raiz: na raiz ela seria carregada tambem
    /// como proxy por qualquer outro mod que procure ReShade64.dll ali, e teriamos duas
    /// instancias do ReShade no mesmo processo.
    /// </summary>
    public static async Task<bool> DeployAsync(ReShadeService reshade, string targetDir,
                                               bool jogo64Bits, IProgress<string>? progress = null)
    {
        var dir = LayerDir(targetDir);
        Directory.CreateDirectory(dir);

        var dllName = jogo64Bits ? "ReShade64.dll" : "ReShade32.dll";
        var dep = await reshade.DeployRawAsync(dir, dllName, jogo64Bits, progress);
        if (!dep.Success) return false;

        var dll = Path.Combine(dir, dllName);
        var json = ManifestPath(targetDir);

        // Caminho absoluto, com as barras escapadas para JSON. Ver o item 1 do cabecalho: um
        // caminho relativo aqui nao degrada, derruba todo aplicativo Vulkan da maquina.
        var libEscapado = dll.Replace("\\", "\\\\");
        File.WriteAllText(json, $$"""
        {
          "file_format_version": "1.2.0",
          "layer": {
            "name": "{{LayerName}}",
            "type": "GLOBAL",
            "library_path": "{{libEscapado}}",
            "api_version": "1.3.0",
            "implementation_version": "1",
            "description": "ReShade",
            "functions": {
              "vkNegotiateLoaderLayerInterfaceVersion": "vkNegotiateLoaderLayerInterfaceVersion"
            },
            "disable_environment": {
              "DISABLE_{{LayerName}}": "1"
            }
          }
        }
        """);

        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(jogo64Bits ? Key64 : Key32);
            if (k is null) return false;
            k.SetValue(json, 0, RegistryValueKind.DWord);   // 0 = habilitada
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn("vulkan layer: sem permissao para escrever em HKLM (precisa de administrador)");
            return false;
        }
        catch (Exception ex) { Log.Warn($"vulkan layer: {ex.Message}"); return false; }

        Log.Info($"vulkan layer registrada ({(jogo64Bits ? "x64" : "x86")}): {json}");
        progress?.Report(L.T("Vulkan_LayerRegistered"));
        return true;
    }

    /// <summary>Tira o registro e os arquivos. Deixar a entrada apontando para um arquivo que
    /// nao existe mais quebraria todo aplicativo Vulkan, entao a remocao nao e opcional.</summary>
    public static void Remove(string targetDir)
    {
        var json = ManifestPath(targetDir);
        foreach (var (raiz, sub) in new[] { (Registry.LocalMachine, Key64), (Registry.LocalMachine, Key32) })
        {
            try
            {
                using var k = raiz.OpenSubKey(sub, writable: true);
                if (k?.GetValue(json) is not null) k.DeleteValue(json, throwOnMissingValue: false);
            }
            catch (Exception ex) { Log.Warn($"vulkan layer remove: {ex.Message}"); }
        }
        try
        {
            var dir = LayerDir(targetDir);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { Log.Warn($"vulkan layer dir: {ex.Message}"); }
    }

    private static bool ContemTexto(string path, string alvo)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[1 << 20];
            var carry = alvo.Length - 1;
            var anterior = new byte[carry];
            var temAnterior = false;
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                var janela = temAnterior ? anterior.Concat(buf.Take(n)).ToArray() : buf.Take(n).ToArray();
                if (System.Text.Encoding.ASCII.GetString(janela)
                        .Contains(alvo, StringComparison.OrdinalIgnoreCase)) return true;
                if (n >= carry) { Array.Copy(buf, n - carry, anterior, 0, carry); temAnterior = true; }
            }
        }
        catch (Exception ex) { Log.Warn($"vulkan probe {path}: {ex.Message}"); }
        return false;
    }
}
