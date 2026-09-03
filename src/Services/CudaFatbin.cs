using System.IO;

namespace RenoDXLauncher.Services;

/// <summary>
/// Para quais placas um `nvngx_dlssnr.dll` foi compilado — lido de dentro do arquivo.
///
/// O runtime neural e uma biblioteca CUDA: o codigo de GPU vai embutido em registros `fatbin`, um
/// por arquitetura (`sm_75` Turing, `sm_86` Ampere, `sm_89` Ada, `sm_120` Blackwell). O build que
/// a NVIDIA publicou tem SO `sm_120`, e e por isso que ele "nao faz nada" numa RTX 40: nao ha
/// kernel para carregar, e ninguem reporta erro — nem o addon, nem o jogo, nem o log.
///
/// A comunidade recompila esses kernels, e cada rebuild cobre um conjunto diferente. Uma tabela
/// fixa de "qual versao serve qual placa" envelhece a cada release nova; ler o proprio arquivo,
/// nao. A leitura e a autoridade, e a tabela de preferencia (ver
/// <see cref="NeuralUpliftService.RuntimesParaEstaPlaca"/>) so decide a ORDEM de tentativa.
///
/// O formato: cada registro comeca com o magic 0xBA55ED50, seguido de um cabecalho cujo tamanho
/// esta em +6 (UInt16) e cujo corpo tem o tamanho em +8 (UInt64). Dentro do corpo vem as entradas,
/// cada uma com o tamanho do proprio cabecalho em +4 (UInt32) e o do payload em +8 (UInt64); o
/// numero da arquitetura mora em +24 (ou +28/+20, que variam com a versao do formato). Os cubins
/// sao comprimidos, entao o `sm` do cabecalho da entrada e a unica leitura confiavel — abrir o
/// ELF de dentro exigiria descomprimir 165 MB.
///
/// Metodo tirado do DLSS5-Autopilot (MIT, core/gpu.py), que resolveu o mesmo problema antes.
/// </summary>
public static class CudaFatbin
{
    /// <summary>Arquiteturas que reconhecemos como numero de `sm` valido. A lista existe para o
    /// parser saber quando esta lendo um campo de arquitetura e quando esta lendo lixo — um
    /// inteiro qualquer no offset certo passaria sem ela.</summary>
    private static readonly int[] SmConhecidos =
        { 50, 52, 53, 60, 61, 62, 70, 72, 75, 80, 86, 87, 89, 90, 100, 101, 120, 121 };

    /// <summary>Nome humano de uma arquitetura, para a mensagem que o usuario le.</summary>
    public static string Rotulo(int sm) => sm switch
    {
        75 => "RTX 20 / GTX 16 (Turing)",
        80 => "A100 (Ampere)",
        86 => "RTX 30 (Ampere)",
        87 => "Orin",
        89 => "RTX 40 (Ada Lovelace)",
        90 => "H100 (Hopper)",
        100 or 101 => "Blackwell (data center)",
        120 or 121 => "RTX 50 (Blackwell)",
        _ => $"sm_{sm}",
    };

    /// <summary>
    /// A arquitetura CUDA desta placa, pelo nome do adaptador.
    ///
    /// Serie decide: 50xx e Blackwell (sm_120), 40xx e Ada (sm_89), 30xx e Ampere (sm_86), 20xx e
    /// a serie 16xx sao Turing (sm_75). Abaixo disso nao ha tensor core que sirva, e o numero so
    /// existe para a mensagem dizer o que a placa e em vez de "desconhecida".
    ///
    /// As placas de estacao de trabalho ficam de fora de proposito: "RTX 5000 Ada Generation" tem
    /// quatro digitos comecando em 5 e NAO e Blackwell — o mesmo engano que
    /// <see cref="NeuralUpliftService.IsBlackwell"/> ja corrigia.
    /// </summary>
    public static int? SmDoNome(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return null;
        var palavras = gpuName.Split(new[] { ' ', '\t', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        if (palavras.Contains("Blackwell", StringComparer.OrdinalIgnoreCase)) return 120;
        // Ada e Quadro: linha profissional, cuja numeracao nao segue a da GeForce.
        if (palavras.Contains("Ada", StringComparer.OrdinalIgnoreCase)) return 89;
        if (palavras.Contains("Quadro", StringComparer.OrdinalIgnoreCase)) return null;

        var m = System.Text.RegularExpressions.Regex.Match(
            gpuName, @"\b(?:RTX|GTX)\s*(\d{3,4})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n)) return null;
        return n switch
        {
            >= 5000 and <= 5999 => 120,
            >= 4000 and <= 4999 => 89,
            >= 3000 and <= 3999 => 86,
            >= 2000 and <= 2999 => 75,
            >= 1600 and <= 1699 => 75,   // GTX 16xx: Turing sem tensor core, mas a arquitetura e essa
            _ => null,
        };
    }

    /// <summary>
    /// As arquiteturas para as quais este arquivo traz codigo. Conjunto vazio = nao deu para ler
    /// (arquivo ausente, sem registro fatbin, ou formato que este parser nao reconhece), e nesse
    /// caso quem chama NAO deve concluir nada: silencio aqui e falta de evidencia, nao evidencia
    /// de falta.
    /// </summary>
    public static IReadOnlySet<int> Arquiteturas(string dllPath)
    {
        var achadas = new HashSet<int>();
        try
        {
            using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            foreach (var inicio in Magics(fs))
            {
                LerRegistro(fs, inicio, achadas);
                // Um runtime tem dezenas de registros e no maximo meia duzia de arquiteturas;
                // depois de ver as quatro que interessam nao ha o que ganhar varrendo 165 MB.
                if (achadas.Count >= 6) break;
            }
        }
        catch (Exception ex) { Log.Warn($"fatbin {dllPath}: {ex.Message}"); }
        return achadas;
    }

    /// <summary>Offsets do magic 0xBA55ED50 no arquivo, lidos em blocos com sobreposicao para o
    /// magic nao se perder na emenda entre dois blocos.</summary>
    private static IEnumerable<long> Magics(FileStream fs)
    {
        const int Bloco = 8 << 20;
        var buf = new byte[Bloco];
        long pos = 0;
        var limite = fs.Length;
        while (pos < limite)
        {
            fs.Position = pos;
            var n = fs.Read(buf, 0, Bloco);
            if (n < 4) yield break;
            for (var i = 0; i + 4 <= n; i++)
                if (buf[i] == 0x50 && buf[i + 1] == 0xED && buf[i + 2] == 0x55 && buf[i + 3] == 0xBA)
                    yield return pos + i;
            pos += n - 3;
        }
    }

    /// <summary>Le um registro fatbin e junta as arquiteturas das entradas dele.</summary>
    private static void LerRegistro(FileStream fs, long inicio, HashSet<int> achadas)
    {
        var cab = Ler(fs, inicio, 16);
        if (cab is null) return;
        int tamCabecalho = BitConverter.ToUInt16(cab, 6);
        long tamCorpo = (long)BitConverter.ToUInt64(cab, 8);
        if (tamCabecalho < 16 || tamCorpo <= 0 || tamCorpo > fs.Length) return;

        long p = inicio + tamCabecalho, fim = p + tamCorpo;
        // Teto de entradas: um arquivo corrompido (ou um falso positivo do magic) nao pode
        // transformar a leitura num laco de milhoes de iteracoes.
        for (var guarda = 0; guarda < 4096 && p < fim - 32; guarda++)
        {
            var e = Ler(fs, p, 32);
            if (e is null) return;
            long tamCabEntrada = BitConverter.ToUInt32(e, 4);
            long tamPayload = (long)BitConverter.ToUInt64(e, 8);
            if (tamCabEntrada < 24 || tamCabEntrada > 4096 || tamPayload <= 0 || tamPayload > fs.Length) return;

            foreach (var off in new[] { 24, 28, 20 })
            {
                if (off + 4 > e.Length) continue;
                var sm = (int)BitConverter.ToUInt32(e, off);
                if (!SmConhecidos.Contains(sm)) continue;
                achadas.Add(sm);
                break;
            }
            p += tamCabEntrada + tamPayload;
        }
    }

    private static byte[]? Ler(FileStream fs, long offset, int quantos)
    {
        if (offset < 0 || offset + quantos > fs.Length) return null;
        var buf = new byte[quantos];
        fs.Position = offset;
        var lido = 0;
        while (lido < quantos)
        {
            var n = fs.Read(buf, lido, quantos - lido);
            if (n <= 0) return null;
            lido += n;
        }
        return buf;
    }
}
