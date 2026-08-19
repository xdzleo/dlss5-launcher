using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RenoDXLauncher.Services;

/// <summary>
/// Verificacao de assinatura Authenticode, via WinVerifyTrust (a mesma API que o Windows
/// usa no Explorer e no SmartScreen).
///
/// Existe por um motivo so: provar que o ReShade que o launcher baixou e extraiu veio
/// mesmo do autor do ReShade. Sem isso, o que o app faz e "baixa um executavel da
/// internet e usa o binario de dentro dele" - que e a descricao literal de um dropper, e
/// e a frase que perde uma disputa de falso-positivo com fabricante de antivirus.
/// </summary>
public static class Authenticode
{
    // ---- WinVerifyTrust -----------------------------------------------------

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE               = 2;
    private const uint WTD_REVOKE_NONE           = 0;
    private const uint WTD_CHOICE_FILE           = 1;
    private const uint WTD_STATEACTION_VERIFY    = 1;
    private const uint WTD_STATEACTION_CLOSE     = 2;
    // WTD_SAFER_FLAG (0x100) existe, e NAO deve ser usado aqui: com ele ligado, arquivo
    // adulterado passa a retornar TRUST_E_NOSIGNATURE em vez de TRUST_E_BAD_DIGEST — medido.
    // O veredito final seria o mesmo (recusar), mas a mensagem viraria "nao assinado" para
    // um arquivo que na verdade foi modificado no caminho, que e exatamente o diagnostico
    // que alguem precisa quando um proxy ou antivirus esta reescrevendo o download.
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    private const uint S_OK                  = 0x00000000;
    private const uint TRUST_E_NOSIGNATURE   = 0x800B0100;
    private const uint TRUST_E_BAD_DIGEST    = 0x80096010;
    private const uint CERT_E_UNTRUSTEDROOT  = 0x800B0109;
    private const uint CERT_E_CHAINING       = 0x800B010A;
    private const uint CERT_E_EXPIRED        = 0x800B0101;
    private const uint CERT_E_UNTRUSTEDTESTROOT = 0x800B010D;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    /// <param name="DigestIntact">
    /// O conteudo do arquivo bate com o que foi assinado. Falso aqui significa arquivo
    /// adulterado depois da assinatura, ou sem assinatura nenhuma.
    /// </param>
    /// <param name="ChainTrusted">
    /// A cadeia sobe ate uma raiz confiavel do Windows. Falso e ESPERADO para certificado
    /// auto-assinado - nao e sinal de adulteracao.
    /// </param>
    public readonly record struct Result(bool DigestIntact, bool ChainTrusted, string? Sha256Thumbprint, string? Subject, string Detail);

    public static Result Verify(string filePath)
    {
        if (!File.Exists(filePath))
            return new Result(false, false, null, null, "arquivo nao existe");

        var fileInfo = new WinTrustFileInfo
        {
            cbStruct       = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath  = filePath,
            hFile          = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var pData = IntPtr.Zero;
        uint rc;
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct            = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice          = WTD_UI_NONE,
                // Sem consulta de revogacao: ela sai para a rede, e essa verificacao roda
                // no meio de uma instalacao. Cert auto-assinado nao tem CRL de qualquer forma.
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice       = WTD_CHOICE_FILE,
                pFile               = pFile,
                dwStateAction       = WTD_STATEACTION_VERIFY,
                dwProvFlags         = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            pData = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, pData, false);

            rc = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);

            // Sempre fechar o estado, ou o wintrust vaza handle por chamada.
            var close = Marshal.PtrToStructure<WinTrustData>(pData);
            close.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(close, pData, false);
            WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);
        }
        finally
        {
            if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
            Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }

        // A distincao que importa: raiz nao confiavel / cadeia incompleta / cert expirado
        // significam que o DIGESTO estava certo e so a confianca da cadeia falhou. Ja
        // TRUST_E_BAD_DIGEST significa conteudo alterado, e TRUST_E_NOSIGNATURE, sem assinatura.
        bool digestIntact = rc switch
        {
            S_OK or CERT_E_UNTRUSTEDROOT or CERT_E_CHAINING or CERT_E_EXPIRED or CERT_E_UNTRUSTEDTESTROOT => true,
            _ => false,
        };
        bool chainTrusted = rc == S_OK;

        string detail = rc switch
        {
            S_OK                    => "assinatura valida e cadeia confiavel",
            CERT_E_UNTRUSTEDROOT    => "assinatura integra, raiz nao confiavel (certificado auto-assinado)",
            CERT_E_UNTRUSTEDTESTROOT => "assinatura integra, raiz de teste",
            CERT_E_CHAINING         => "assinatura integra, cadeia incompleta",
            CERT_E_EXPIRED          => "assinatura integra, certificado expirado",
            TRUST_E_NOSIGNATURE     => "arquivo NAO assinado",
            TRUST_E_BAD_DIGEST      => "arquivo ADULTERADO depois de assinado",
            _                       => $"WinVerifyTrust retornou 0x{rc:X8}",
        };

        string? thumb = null, subject = null;
        if (digestIntact)
        {
            try
            {
                // SYSLIB0057 manda usar X509CertificateLoader — mas ele carrega BYTES de
                // certificado, e nao extrai o signatario de um PE assinado. CreateFromSignedFile
                // continua sendo a unica forma de obter o certificado embutido no arquivo, entao
                // o caminho e: extrair com ela, e carregar os bytes com o loader novo.
#pragma warning disable SYSLIB0057
                var raw = X509Certificate.CreateFromSignedFile(filePath).GetRawCertData();
#pragma warning restore SYSLIB0057
                using var cert = X509CertificateLoader.LoadCertificate(raw);
                thumb   = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
                subject = cert.Subject;
            }
            catch (Exception ex) { detail += $" (nao consegui ler o certificado: {ex.Message})"; }
        }

        return new Result(digestIntact, chainTrusted, thumb, subject, detail);
    }

    /// <summary>
    /// Confere que o arquivo esta integro E que quem assinou e exatamente o certificado
    /// esperado (comparado pelo SHA-256 do proprio certificado).
    ///
    /// Fixar o certificado, e nao o hash do arquivo, e o que faz esta verificacao continuar
    /// valendo quando o ReShade lancar a proxima versao: o hash muda a cada release, a
    /// identidade de quem assina nao.
    /// </summary>
    public static bool IsSignedBy(string filePath, string expectedSha256Thumbprint, out string detail)
    {
        var r = Verify(filePath);
        if (!r.DigestIntact)
        {
            detail = r.Detail;
            return false;
        }
        if (!string.Equals(r.Sha256Thumbprint, expectedSha256Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"assinado por outro certificado: {r.Subject ?? "?"} (SHA-256 {r.Sha256Thumbprint ?? "?"})";
            return false;
        }
        detail = $"{r.Detail}; signatario conferido: {r.Subject}";
        return true;
    }
}
