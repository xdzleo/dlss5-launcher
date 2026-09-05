using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RenoDXLauncher.Services;

/// <summary>
/// A porta unica por onde toda escrita na pasta de um jogo passa.
///
/// Antes disto, cada servico guardava o que substituia do seu jeito e ao lado do arquivo:
/// `.renodx-bak`, `.pre-dxvk`, `.renodx-ours`, o `anterior/` do Feeder. Funcionava por arquivo e
/// nao respondia a pergunta que importa — "devolve esta pasta ao que ela era" — porque ninguem
/// tinha a lista inteira. Um arquivo que um servico apagou e outro reescreveu perdia o rastro, e
/// desligar tudo deixava restos que so apareciam quando o jogo quebrava.
///
/// Agora ha um registro so, dentro da propria pasta do jogo:
///
///   _DLSS5_Backup\
///     manifesto.json   o que a pasta era: o que substituimos e o que acrescentamos
///     diario.log       toda operacao, em ordem, com hora, tamanho e hash
///     &lt;caminho&gt;      o arquivo ORIGINAL, bit a bit
///
/// Dentro da pasta do jogo de proposito: o backup viaja com o jogo se ele mudar de biblioteca,
/// sobrevive a uma reinstalacao do launcher, e some junto se a pessoa apagar o jogo. Um backup
/// guardado em AppData teria as tres propriedades invertidas.
///
/// Duas regras que fazem o restaurar valer alguma coisa:
///
/// 1. O ORIGINAL e gravado uma vez so. A segunda instalacao ja encontra a entrada e nao mexe
///    nela — senao o "original" viraria o arquivo que nos mesmos pusemos na primeira, e restaurar
///    devolveria a nossa versao com cara de original.
/// 2. O que a pasta NAO tinha e marcado como acrescentado, e restaurar apaga. Um arquivo nao pode
///    ser as duas coisas: quem foi acrescentado nunca vira substituido.
/// </summary>
public static class BackupService
{
    public const string PastaDeBackup = "_DLSS5_Backup";
    private const string NomeDoManifesto = "manifesto.json";
    private const string NomeDoDiario = "diario.log";

    /// <param name="Rel">Caminho relativo a pasta do jogo. E a chave: e o que restaurar precisa.</param>
    /// <param name="Sha256">Hash do arquivo ORIGINAL, para o restaurar poder se conferir.</param>
    /// <param name="Bytes">Tamanho do original. Junto com o hash, prova a devolucao.</param>
    /// <param name="Quando">Quando foi guardado.</param>
    /// <param name="Quem">Que parte do launcher mexeu — aparece no diario e no relatorio.</param>
    public record Substituido(string Rel, string Sha256, long Bytes, DateTime Quando, string Quem);

    /// <param name="Rel">Caminho relativo do arquivo que a pasta nao tinha.</param>
    public record Acrescentado(string Rel, DateTime Quando, string Quem);

    public class Manifesto
    {
        public int Versao { get; set; } = 1;
        public string? Jogo { get; set; }
        public DateTime Inicio { get; set; } = DateTime.UtcNow;
        public List<Substituido> Substituidos { get; set; } = [];
        public List<Acrescentado> Acrescentados { get; set; } = [];
        public List<string> PastasCriadas { get; set; } = [];
    }

    public static string RaizDoBackup(string pastaDoJogo) => Path.Combine(pastaDoJogo, PastaDeBackup);
    private static string CaminhoDoManifesto(string pastaDoJogo) =>
        Path.Combine(RaizDoBackup(pastaDoJogo), NomeDoManifesto);
    private static string CaminhoDoDiario(string pastaDoJogo) =>
        Path.Combine(RaizDoBackup(pastaDoJogo), NomeDoDiario);

    /// <summary>Ha o que restaurar nesta pasta?</summary>
    public static bool TemBackup(string? pastaDoJogo) =>
        pastaDoJogo is not null && File.Exists(CaminhoDoManifesto(pastaDoJogo));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Manifesto Ler(string pastaDoJogo)
    {
        try
        {
            var p = CaminhoDoManifesto(pastaDoJogo);
            if (!File.Exists(p)) return new Manifesto { Jogo = pastaDoJogo };
            return JsonSerializer.Deserialize<Manifesto>(File.ReadAllText(p)) ?? new Manifesto { Jogo = pastaDoJogo };
        }
        catch (Exception ex)
        {
            // Um manifesto ilegivel nao pode virar um manifesto vazio: seria o mesmo que dizer
            // "esta pasta esta intacta" sobre uma pasta em que ja mexemos, e o proximo backup
            // gravaria os NOSSOS arquivos como se fossem os originais.
            Log.Warn($"backup: manifesto ilegivel em {pastaDoJogo}: {ex.Message}");
            throw new InvalidOperationException(Localization.L.T("Backup_ManifestoIlegivel"));
        }
    }

    private static void Gravar(string pastaDoJogo, Manifesto m)
    {
        try
        {
            Directory.CreateDirectory(RaizDoBackup(pastaDoJogo));
            File.WriteAllText(CaminhoDoManifesto(pastaDoJogo), JsonSerializer.Serialize(m, Json));
        }
        catch (Exception ex) { Log.Warn($"backup: gravar manifesto em {pastaDoJogo}: {ex.Message}"); }
    }

    /// <summary>
    /// O diario.
    ///
    /// Uma linha por operacao, em texto, na pasta do jogo. Nao e o log do launcher: aquele conta a
    /// historia do programa, este conta a historia DESTA pasta — e e o que se manda junto quando
    /// alguem pergunta "o que foi que voce fez aqui?".
    /// </summary>
    public static void Anotar(string pastaDoJogo, string quem, string acao, string? detalhe = null)
    {
        try
        {
            Directory.CreateDirectory(RaizDoBackup(pastaDoJogo));
            var linha = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {quem,-16} {acao}"
                        + (detalhe is null ? "" : $"  {detalhe}");
            File.AppendAllText(CaminhoDoDiario(pastaDoJogo), linha + Environment.NewLine);
        }
        catch (Exception ex) { Log.Warn($"backup: diario em {pastaDoJogo}: {ex.Message}"); }
    }

    /// <summary>Le o diario inteiro, para a interface mostrar.</summary>
    public static IReadOnlyList<string> LerDiario(string pastaDoJogo)
    {
        try
        {
            var p = CaminhoDoDiario(pastaDoJogo);
            return File.Exists(p) ? File.ReadAllLines(p) : [];
        }
        catch (Exception ex) { Log.Warn($"backup: ler diario {pastaDoJogo}: {ex.Message}"); return []; }
    }

    /// <summary>
    /// Chame ANTES de escrever qualquer coisa dentro da pasta de um jogo.
    ///
    /// Se o arquivo existe, o original vai para o backup (uma vez so, ver a regra 1 na classe).
    /// Se nao existe, fica anotado como acrescentado, para o restaurar saber que ele tem de sair.
    ///
    /// Nao lanca: falhar em guardar o backup nao pode impedir a instalacao — mas fica no diario e
    /// no log, e o restaurar depois diz que aquele arquivo nao tem volta.
    /// </summary>
    /// <param name="quem">Quem esta escrevendo: "neural", "feeder", "dxvk", "reshade"...</param>
    public static void AntesDeEscrever(string? pastaDoJogo, string caminhoAbsoluto, string quem)
    {
        if (string.IsNullOrEmpty(pastaDoJogo)) return;
        try
        {
            var rel = Relativo(pastaDoJogo, caminhoAbsoluto);
            // O proprio backup nao entra no backup, e nada de fora da pasta do jogo entra.
            if (rel is null || rel.StartsWith(PastaDeBackup, StringComparison.OrdinalIgnoreCase)) return;

            var m = Ler(pastaDoJogo);
            if (JaConhecido(m, rel)) return;

            if (File.Exists(caminhoAbsoluto))
            {
                var destino = Path.Combine(RaizDoBackup(pastaDoJogo), rel);
                Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
                File.Copy(caminhoAbsoluto, destino, overwrite: true);
                var fi = new FileInfo(caminhoAbsoluto);
                var sha = Hash(caminhoAbsoluto);
                m.Substituidos.Add(new Substituido(rel, sha, fi.Length, DateTime.UtcNow, quem));
                Anotar(pastaDoJogo, quem, "guardou o original", $"{rel}  {fi.Length} bytes  sha {sha[..16]}");
            }
            else
            {
                m.Acrescentados.Add(new Acrescentado(rel, DateTime.UtcNow, quem));
                foreach (var pasta in PastasQueVaoNascer(pastaDoJogo, caminhoAbsoluto))
                    if (!m.PastasCriadas.Contains(pasta, StringComparer.OrdinalIgnoreCase))
                        m.PastasCriadas.Add(pasta);
                Anotar(pastaDoJogo, quem, "vai acrescentar", rel);
            }
            m.Jogo ??= pastaDoJogo;
            Gravar(pastaDoJogo, m);
        }
        catch (Exception ex) { Log.Warn($"backup: antes de escrever {caminhoAbsoluto}: {ex.Message}"); }
    }

    /// <summary>Registra no diario o que ACABOU de ser escrito. Separado do AntesDeEscrever
    /// porque so depois se sabe o tamanho e o hash do que entrou.</summary>
    public static void DepoisDeEscrever(string? pastaDoJogo, string caminhoAbsoluto, string quem)
    {
        if (string.IsNullOrEmpty(pastaDoJogo)) return;
        try
        {
            var rel = Relativo(pastaDoJogo, caminhoAbsoluto);
            if (rel is null || rel.StartsWith(PastaDeBackup, StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(caminhoAbsoluto)) { Anotar(pastaDoJogo, quem, "apagou", rel); return; }
            var fi = new FileInfo(caminhoAbsoluto);
            Anotar(pastaDoJogo, quem, "escreveu", $"{rel}  {fi.Length} bytes  sha {Hash(caminhoAbsoluto)[..16]}");
        }
        catch (Exception ex) { Log.Warn($"backup: depois de escrever {caminhoAbsoluto}: {ex.Message}"); }
    }

    /// <summary>Um arquivo ja tem historia neste manifesto? Substituido OU acrescentado —
    /// a primeira resposta e a que vale para sempre (regra 1 e 2 da classe).</summary>
    private static bool JaConhecido(Manifesto m, string rel) =>
        m.Substituidos.Any(s => s.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase))
        || m.Acrescentados.Any(a => a.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase));

    /// <param name="Devolvidos">Arquivos que voltaram ao conteudo original.</param>
    /// <param name="Apagados">Arquivos nossos que sairam.</param>
    /// <param name="Faltando">Originais que o backup nao tem — nao ha o que devolver.</param>
    /// <param name="Divergentes">Voltaram, mas o hash nao bate: o backup foi mexido por fora.</param>
    public record Resultado(int Devolvidos, int Apagados, List<string> Faltando, List<string> Divergentes);

    /// <summary>
    /// Devolve a pasta ao estado anterior a primeira vez que o launcher escreveu nela.
    ///
    /// A ordem importa: primeiro os originais voltam, depois o que acrescentamos sai, e so entao
    /// as pastas vazias que criamos. Ao contrario, apagar um arquivo poderia levar junto a pasta
    /// onde o original ainda ia ser gravado.
    ///
    /// Cada devolucao e CONFERIDA pelo hash guardado. Sem isso, "restaurado" seria uma palavra
    /// sobre uma copia que ninguem olhou — e o ponto inteiro deste recurso e a pessoa poder dizer
    /// que a pasta esta como estava, bit a bit.
    /// </summary>
    public static Resultado Restaurar(string pastaDoJogo, IProgress<string>? progress = null)
    {
        // Os manifestos das subpastas primeiro.
        //
        // Uma instalacao pode escrever em mais de uma pasta — o caminho de 32 bits monta um
        // `host64\` com addon e runtime proprios, e aquilo vira um registro separado porque
        // aquela pasta e o "alvo" daquele pedaco. Restaurar so o de cima deixaria o host64
        // inteiro para tras, e o botao teria mentido.
        var aninhados = new List<Resultado>();
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(pastaDoJogo, PastaDeBackup,
                                                               SearchOption.AllDirectories))
            {
                var dono = Path.GetDirectoryName(sub)!;
                if (string.Equals(Path.GetFullPath(dono), Path.GetFullPath(pastaDoJogo),
                                  StringComparison.OrdinalIgnoreCase)) continue;
                if (!TemBackup(dono)) continue;
                aninhados.Add(Restaurar(dono, progress));
            }
        }
        catch (Exception ex) { Log.Warn($"backup aninhados de {pastaDoJogo}: {ex.Message}"); }

        var m = Ler(pastaDoJogo);
        var faltando = new List<string>();
        var divergentes = new List<string>();
        int devolvidos = 0, apagados = 0;

        Anotar(pastaDoJogo, "restaurar", "comecou",
               $"{m.Substituidos.Count} para devolver, {m.Acrescentados.Count} para apagar");

        foreach (var s in m.Substituidos)
        {
            var guardado = Path.Combine(RaizDoBackup(pastaDoJogo), s.Rel);
            var alvo = Path.Combine(pastaDoJogo, s.Rel);
            if (!File.Exists(guardado)) { faltando.Add(s.Rel); Anotar(pastaDoJogo, "restaurar", "SEM BACKUP", s.Rel); continue; }
            // O arquivo volta ao conteudo de antes; o registro de mod instalado que apontava para
            // ele tem de sair pelo mesmo motivo do caso acima.
            InstalledModRegistry.Remove(alvo);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(alvo)!);
                File.Copy(guardado, alvo, overwrite: true);
                var agora = Hash(alvo);
                if (!agora.Equals(s.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    divergentes.Add(s.Rel);
                    Anotar(pastaDoJogo, "restaurar", "HASH NAO BATE", $"{s.Rel}  esperado {s.Sha256[..16]}  veio {agora[..16]}");
                }
                else
                {
                    devolvidos++;
                    Anotar(pastaDoJogo, "restaurar", "devolveu o original", $"{s.Rel}  sha {agora[..16]}");
                }
                progress?.Report(s.Rel);
            }
            catch (Exception ex) { faltando.Add(s.Rel); Log.Warn($"backup restaurar {s.Rel}: {ex.Message}"); }
        }

        foreach (var a in m.Acrescentados)
        {
            var alvo = Path.Combine(pastaDoJogo, a.Rel);
            try
            {
                if (File.Exists(alvo)) { File.Delete(alvo); apagados++; Anotar(pastaDoJogo, "restaurar", "apagou o nosso", a.Rel); }
                // O launcher tem uma segunda memoria do que instalou, em installed.json, e ela
                // nao mora na pasta do jogo. Apagar o arquivo sem apagar o registro deixava a
                // tela achando que o mod continua instalado — o interruptor seguia LIGADO sobre
                // uma pasta de onde o addon acabou de sair.
                InstalledModRegistry.Remove(alvo);
            }
            catch (Exception ex) { Log.Warn($"backup apagar {a.Rel}: {ex.Message}"); }
        }

        // Da mais funda para a mais rasa, e so as vazias: o que o jogo ou a pessoa puseram dentro
        // fica, e a pasta com eles fica junto.
        foreach (var p in m.PastasCriadas.OrderByDescending(x => x.Length))
        {
            var alvo = Path.Combine(pastaDoJogo, p);
            try
            {
                if (Directory.Exists(alvo) && !Directory.EnumerateFileSystemEntries(alvo).Any())
                {
                    Directory.Delete(alvo);
                    Anotar(pastaDoJogo, "restaurar", "apagou a pasta vazia", p);
                }
            }
            catch (Exception ex) { Log.Warn($"backup rmdir {p}: {ex.Message}"); }
        }

        Anotar(pastaDoJogo, "restaurar", "terminou",
               $"{devolvidos} devolvidos, {apagados} apagados, {faltando.Count} sem backup, {divergentes.Count} divergentes");

        // A camada Vulkan nao e um arquivo: e uma chave em HKLM apontando para o manifesto que
        // pusemos na pasta. Restaurar sem tira-la deixaria o sistema registrando uma camada cujo
        // json acabou de ser apagado — e "a pasta esta como estava" nao cobre uma sujeira que
        // ficou FORA dela. Remove e no-op quando nao ha nada registrado.
        try
        {
            VulkanLayerService.Remove(pastaDoJogo);
            Anotar(pastaDoJogo, "restaurar", "tirou a camada Vulkan do registro do Windows");
        }
        catch (Exception ex) { Log.Warn($"backup camada vulkan {pastaDoJogo}: {ex.Message}"); }

        // E por fim o proprio backup sai da pasta do jogo.
        //
        // Ele tambem e uma coisa que a pasta nao tinha. Deixa-lo ali seria entregar uma pasta
        // "restaurada" com uma subpasta nossa dentro e centenas de MB de copias — e a promessa
        // do botao e que a pasta fique como estava, sem asterisco.
        //
        // So quando o restaurar fechou LIMPO. Se faltou original ou algum hash nao bateu, as
        // copias sao a unica chance de acertar a mao depois, e apaga-las seria trocar um
        // problema pequeno por um irreversivel.
        //
        // O diario nao se perde: ele vai para a pasta do launcher antes, porque a historia de
        // quem mexeu no que continua valendo depois de a pasta voltar ao normal.
        try
        {
            var p = CaminhoDoManifesto(pastaDoJogo);
            if (File.Exists(p))
                File.Move(p, p + $".restaurado-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);

            if (faltando.Count == 0 && divergentes.Count == 0)
            {
                GuardarDiarioNoLauncher(pastaDoJogo);
                Directory.Delete(RaizDoBackup(pastaDoJogo), recursive: true);
                Log.Info($"backup: {PastaDeBackup} removido de {pastaDoJogo} (restauracao limpa)");
            }
            else
            {
                Log.Info($"backup: {PastaDeBackup} mantido em {pastaDoJogo}: "
                         + $"{faltando.Count} sem backup, {divergentes.Count} divergentes");
            }
        }
        catch (Exception ex) { Log.Warn($"backup aposentar manifesto: {ex.Message}"); }

        Log.Info($"backup: {pastaDoJogo} restaurado ({devolvidos} devolvidos, {apagados} apagados)");
        // O que as subpastas devolveram conta no mesmo total: quem clicou no botao pediu a pasta
        // inteira de volta, e nao sabe que existe um host64 la dentro.
        return new Resultado(
            devolvidos + aninhados.Sum(a => a.Devolvidos),
            apagados + aninhados.Sum(a => a.Apagados),
            [.. faltando, .. aninhados.SelectMany(a => a.Faltando)],
            [.. divergentes, .. aninhados.SelectMany(a => a.Divergentes)]);
    }

    // ---------- as operacoes, ja registradas ----------
    //
    // Quem escreve na pasta de um jogo chama estas, e nao File.Copy direto. E a diferenca entre
    // "o launcher guarda backup" e "o launcher guarda backup quando alguem lembrou de chamar":
    // o registro acontece dentro da propria operacao, entao nao ha como escrever sem registrar.

    /// <summary>Copia um arquivo para dentro da pasta do jogo, guardando o que estava la.</summary>
    public static void Copiar(string? pastaDoJogo, string origem, string destino, string quem)
    {
        AntesDeEscrever(pastaDoJogo, destino, quem);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        File.Copy(origem, destino, overwrite: true);
        DepoisDeEscrever(pastaDoJogo, destino, quem);
    }

    /// <summary>Escreve texto dentro da pasta do jogo, guardando o que estava la.</summary>
    public static void Escrever(string? pastaDoJogo, string destino, string conteudo, string quem)
    {
        AntesDeEscrever(pastaDoJogo, destino, quem);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        File.WriteAllText(destino, conteudo);
        DepoisDeEscrever(pastaDoJogo, destino, quem);
    }

    /// <summary>Escreve bytes dentro da pasta do jogo, guardando o que estava la.</summary>
    public static void EscreverBytes(string? pastaDoJogo, string destino, byte[] conteudo, string quem)
    {
        AntesDeEscrever(pastaDoJogo, destino, quem);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        File.WriteAllBytes(destino, conteudo);
        DepoisDeEscrever(pastaDoJogo, destino, quem);
    }

    /// <summary>
    /// Apaga um arquivo da pasta do jogo, guardando o que estava la.
    ///
    /// Guardar antes de APAGAR e o caso que mais importa: um arquivo apagado sem copia nao volta
    /// de lugar nenhum, e e justamente o que "restaurar" promete devolver.
    /// </summary>
    public static void Apagar(string? pastaDoJogo, string alvo, string quem)
    {
        if (!File.Exists(alvo)) return;
        AntesDeEscrever(pastaDoJogo, alvo, quem);
        File.Delete(alvo);
        Anotar(pastaDoJogo ?? "", quem, "apagou", Relativo(pastaDoJogo ?? "", alvo) ?? alvo);
    }

    /// <summary>Renomeia dentro da pasta do jogo. Os dois lados entram no registro: o que sai do
    /// nome antigo e o que chega no novo.</summary>
    public static void Mover(string? pastaDoJogo, string origem, string destino, string quem)
    {
        AntesDeEscrever(pastaDoJogo, origem, quem);
        AntesDeEscrever(pastaDoJogo, destino, quem);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        File.Move(origem, destino, overwrite: true);
        DepoisDeEscrever(pastaDoJogo, destino, quem);
    }

    /// <summary>
    /// Leva o diario para a pasta do launcher antes de o backup ser apagado.
    ///
    /// A pasta do jogo volta a ser do jogo, mas o registro de quem mexeu no que nao e do jogo —
    /// e nosso, e e o que responde "o que voce fez ali?" seis meses depois.
    /// </summary>
    private static void GuardarDiarioNoLauncher(string pastaDoJogo)
    {
        try
        {
            var origem = CaminhoDoDiario(pastaDoJogo);
            if (!File.Exists(origem)) return;
            var destinoDir = Path.Combine(AppPaths.DataDir, "historico-de-pastas");
            Directory.CreateDirectory(destinoDir);
            var nome = new string(Path.GetFileName(pastaDoJogo.TrimEnd(Path.DirectorySeparatorChar))
                                      .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
                                      .ToArray());
            File.Copy(origem, Path.Combine(destinoDir, $"{nome}-{DateTime.Now:yyyyMMdd-HHmmss}.log"), overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"backup guardar diario: {ex.Message}"); }
    }

    private static string? Relativo(string pastaDoJogo, string caminho)
    {
        try
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(pastaDoJogo), Path.GetFullPath(caminho));
            // ".." significa fora da pasta do jogo: nao e nosso para guardar nem para apagar.
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : rel;
        }
        catch { return null; }
    }

    /// <summary>As pastas que ainda nao existem no caminho deste arquivo — as que vao nascer
    /// por causa dele, e que o restaurar pode tirar se ficarem vazias.</summary>
    private static IEnumerable<string> PastasQueVaoNascer(string pastaDoJogo, string caminho)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(caminho));
        var raiz = Path.GetFullPath(pastaDoJogo);
        while (dir is not null && dir.Length > raiz.Length && !Directory.Exists(dir))
        {
            var rel = Relativo(pastaDoJogo, dir);
            if (rel is not null) yield return rel;
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static string Hash(string caminho)
    {
        try
        {
            using var fs = File.OpenRead(caminho);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
        }
        catch (Exception ex) { Log.Warn($"backup hash {caminho}: {ex.Message}"); return new string('0', 64); }
    }
}
