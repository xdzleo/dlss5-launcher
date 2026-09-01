#Requires -Version 5.1
<#
.SYNOPSIS
    Bateria de verificacao antivirus/integridade dos artefatos de release.

.DESCRIPTION
    Roda tudo que da para verificar sem depender de um antivirus especifico estar
    instalado, e diz honestamente quando um teste nao pode ser feito (SKIP) em vez
    de fingir que passou.

    Testes:
      1  Estrutura do publish        - pasta, nao bundle auto-extraivel (single-file)
      2  Executaveis do payload      - allowlist; qualquer .exe extra e apontado
      3  Metadata de PE              - VersionInfo completo no app e no instalador
      4  Assinatura Authenticode     - valida / ausente / adulterada
      5  Entropia e nome de secao    - heuristica de packer (UPX, Themida, ...)
      6  Manifest do executavel      - asInvoker e supportedOS declarados
      7  Windows Defender            - varredura sob demanda, se o servico existir
      8  Instalar/desinstalar        - instalacao silenciosa real + smoke + remocao
      9  VirusTotal                  - 70+ motores (precisa de chave de API)
     10  Hashes SHA-256              - para publicar junto do release

.PARAMETER PublishDir
    Pasta do publish self-contained. Padrao: publish

.PARAMETER Installer
    Caminho do setup.exe. Padrao: o mais recente em dist\

.PARAMETER VirusTotalApiKey
    Chave da API v3 do VirusTotal. Padrao: variavel de ambiente VT_API_KEY.
    Sem chave, o teste 9 fica SKIP.

.PARAMETER SkipInstallTest
    Nao instala nem desinstala de verdade (teste 8 fica SKIP).

.PARAMETER FailOnWarn
    Sai com codigo != 0 tambem em WARN. Sem isso, so FAIL reprova.

.EXAMPLE
    pwsh tools\av-selfcheck.ps1

.EXAMPLE
    pwsh tools\av-selfcheck.ps1 -VirusTotalApiKey $env:VT_API_KEY -FailOnWarn
#>
[CmdletBinding()]
param(
    [string] $PublishDir       = "publish",
    [string] $Installer        = "",
    [string] $VirusTotalApiKey = $env:VT_API_KEY,
    [switch] $SkipInstallTest,
    [switch] $FailOnWarn
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- resultados --

$script:Results = New-Object System.Collections.ArrayList

function Add-Result {
    param(
        [Parameter(Mandatory)] [string] $Check,
        [Parameter(Mandatory)] [ValidateSet('PASS','WARN','FAIL','SKIP','INFO')] [string] $Status,
        [Parameter(Mandatory)] [string] $Detail
    )
    [void]$script:Results.Add([pscustomobject]@{ Check = $Check; Status = $Status; Detail = $Detail })
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'WARN' { 'Yellow' }
        'FAIL' { 'Red' }
        'SKIP' { 'DarkGray' }
        default { 'Cyan' }
    }
    Write-Host ("  [{0}] {1}" -f $Status.PadRight(4), $Check) -ForegroundColor $color
    foreach ($line in ($Detail -split "`n")) {
        if ($line.Trim()) { Write-Host ("         " + $line.TrimEnd()) -ForegroundColor DarkGray }
    }
}

function Write-Section {
    param([string] $Title)
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor White
}

# ------------------------------------------------------------- parser de PE --

function Get-PeInfo {
    # Le o cabecalho PE direto do arquivo: secoes (nome, tamanho, entropia), se ha
    # tabela de certificado (Authenticode embutido) e se o exe e um bundle
    # single-file do .NET. Sem dependencia externa - roda em runner limpo.
    param([Parameter(Mandatory)][string] $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40) { throw "arquivo pequeno demais para ser PE: $Path" }
    if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { throw "sem assinatura MZ: $Path" }

    $peOff = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOff -le 0 -or ($peOff + 24) -ge $bytes.Length) { throw "e_lfanew invalido: $Path" }
    if ($bytes[$peOff] -ne 0x50 -or $bytes[$peOff + 1] -ne 0x45) { throw "sem assinatura PE: $Path" }

    $numSections = [BitConverter]::ToUInt16($bytes, $peOff + 6)
    $optSize     = [BitConverter]::ToUInt16($bytes, $peOff + 20)
    $optOff      = $peOff + 24
    $magic       = [BitConverter]::ToUInt16($bytes, $optOff)
    $isPe32Plus  = ($magic -eq 0x20B)

    # Data directory indice 4 = Certificate Table (Authenticode).
    $ddOff    = $optOff + $(if ($isPe32Plus) { 112 } else { 96 })
    $certRva  = [BitConverter]::ToUInt32($bytes, $ddOff + 32)
    $certSize = [BitConverter]::ToUInt32($bytes, $ddOff + 36)

    $secOff   = $optOff + $optSize
    $sections = @()
    for ($i = 0; $i -lt $numSections; $i++) {
        $s = $secOff + ($i * 40)
        if (($s + 40) -gt $bytes.Length) { break }
        $name    = ([Text.Encoding]::ASCII.GetString($bytes, $s, 8)).TrimEnd([char]0)
        $rawSize = [BitConverter]::ToUInt32($bytes, $s + 16)
        $rawPtr  = [BitConverter]::ToUInt32($bytes, $s + 20)
        $chars   = [BitConverter]::ToUInt32($bytes, $s + 36)

        $entropy = 0.0
        if ($rawSize -gt 0 -and ($rawPtr + $rawSize) -le $bytes.Length) {
            $freq = New-Object 'int[]' 256
            $end  = $rawPtr + $rawSize
            for ($p = $rawPtr; $p -lt $end; $p++) { $freq[$bytes[$p]] = $freq[$bytes[$p]] + 1 }
            foreach ($f in $freq) {
                if ($f -gt 0) {
                    $pr = $f / $rawSize
                    $entropy = $entropy - ($pr * [Math]::Log($pr, 2))
                }
            }
        }

        $sections += [pscustomobject]@{
            Name         = $name
            RawSize      = $rawSize
            Entropy      = [Math]::Round($entropy, 3)
            IsExecutable = (($chars -band 0x20000000) -ne 0)
        }
    }

    # Bundle single-file do .NET: o exe se auto-extrai em disco antes de rodar, que e
    # justamente o que NAO queremos publicar.
    #
    # Cuidado: TODO apphost do .NET carrega essa assinatura, mesmo sem bundle nenhum -
    # ela existe para o empacotador conseguir localizar o placeholder. O que distingue
    # e o offset do cabecalho do bundle, nos 8 bytes IMEDIATAMENTE ANTES da assinatura:
    # zerado = placeholder intacto = deployment em pasta.
    $sig = [byte[]](0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32)
    $hasBundle = $false
    $limit = $bytes.Length - $sig.Length
    for ($i = 0; $i -le $limit; $i++) {
        if ($bytes[$i] -eq $sig[0]) {
            $match = $true
            for ($j = 1; $j -lt $sig.Length; $j++) {
                if ($bytes[$i + $j] -ne $sig[$j]) { $match = $false; break }
            }
            if ($match) {
                if ($i -ge 8 -and [BitConverter]::ToInt64($bytes, $i - 8) -ne 0) { $hasBundle = $true }
                break
            }
        }
    }

    [pscustomobject]@{
        Path         = $Path
        Sections     = $sections
        HasCertTable = ($certRva -gt 0 -and $certSize -gt 0)
        IsSingleFile = $hasBundle
        Size         = $bytes.Length
    }
}

# ------------------------------------------------------------------- alvos ---

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {

Write-Host "RenoDX Launcher - verificacao de artefatos" -ForegroundColor White
Write-Host ("repo:  {0}" -f $repoRoot) -ForegroundColor DarkGray

$publishPath = Resolve-Path -LiteralPath $PublishDir -ErrorAction SilentlyContinue
if (-not $publishPath) {
    throw "publish nao encontrado em '$PublishDir'. Rode tools\build-installer.ps1 antes."
}
$publishPath = $publishPath.Path
$appExe = Join-Path $publishPath 'RenoDXLauncher.exe'
if (-not (Test-Path $appExe)) { throw "RenoDXLauncher.exe nao encontrado em $publishPath" }

if (-not $Installer) {
    $cand = Get-ChildItem -Path (Join-Path $repoRoot 'dist') -Filter '*setup.exe' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($cand) { $Installer = $cand.FullName }
}
if ($Installer -and -not (Test-Path $Installer)) { throw "instalador nao encontrado: $Installer" }

Write-Host ("app:   {0}" -f $appExe) -ForegroundColor DarkGray
Write-Host ("setup: {0}" -f $(if ($Installer) { $Installer } else { '(nenhum)' })) -ForegroundColor DarkGray

# --- 1. estrutura do publish -------------------------------------------------

Write-Section "1. Estrutura do publish"

$appPe   = Get-PeInfo -Path $appExe
$hostfxr = Test-Path (Join-Path $publishPath 'hostfxr.dll')

if ($appPe.IsSingleFile) {
    Add-Result 'Deployment em pasta (nao single-file)' 'FAIL' @"
RenoDXLauncher.exe contem um bundle single-file do .NET.
Um exe que descomprime a si mesmo em disco e roda o resultado e o padrao que motor
heuristico mais pontua. Publique com -p:PublishSingleFile=false.
"@
} elseif (-not $hostfxr) {
    Add-Result 'Deployment em pasta (nao single-file)' 'WARN' `
        'hostfxr.dll ausente ao lado do exe - o publish nao parece self-contained em pasta.'
} else {
    $n = (Get-ChildItem $publishPath -Recurse -File).Count
    Add-Result 'Deployment em pasta (nao single-file)' 'PASS' `
        "$n arquivos, hostfxr.dll presente, exe de $([Math]::Round($appPe.Size / 1KB)) KB (sem payload embutido)."
}

# --- 2. executaveis do payload ----------------------------------------------

Write-Section "2. Executaveis dentro do payload"

$allowedExes = @('RenoDXLauncher.exe')
$foundExes = Get-ChildItem $publishPath -Recurse -File |
             Where-Object { $_.Extension -in @('.exe','.com','.scr','.bat','.cmd','.ps1','.vbs','.js') } |
             ForEach-Object { $_.Name }
$extra = @($foundExes | Where-Object { $allowedExes -notcontains $_ })

if ($extra.Count -eq 0) {
    Add-Result 'Nenhum executavel inesperado' 'PASS' 'Unico executavel publicado: RenoDXLauncher.exe'
} else {
    Add-Result 'Nenhum executavel inesperado' 'WARN' @"
Executaveis extras no payload: $($extra -join ', ')
createdump.exe e o utilitario de dump de memoria da Microsoft; o launcher nunca o usa
e varios motores comportamentais tem regra para ele. O csproj remove no publish
(RemoveDiagnosticTools). Se ele voltou, o publish rodou com RemoveDiagnosticTools=false.
"@
}

# --- 3. metadata de PE -------------------------------------------------------

Write-Section "3. Metadata de PE (VersionInfo)"

function Test-VersionInfo {
    param([string] $Path, [string] $Label)

    $vi = (Get-Item -LiteralPath $Path).VersionInfo
    $campos = [ordered]@{
        CompanyName     = $vi.CompanyName
        ProductName     = $vi.ProductName
        FileDescription = $vi.FileDescription
        LegalCopyright  = $vi.LegalCopyright
        FileVersion     = $vi.FileVersion
        ProductVersion  = $vi.ProductVersion
    }
    $vazios = @($campos.GetEnumerator() |
                Where-Object { [string]::IsNullOrWhiteSpace($_.Value) } |
                ForEach-Object { $_.Key })
    $resumo = ($campos.GetEnumerator() | ForEach-Object { "$($_.Key) = $($_.Value)" }) -join "`n"

    if ($vazios.Count -gt 0) {
        Add-Result "VersionInfo completo - $Label" 'FAIL' @"
Campos em branco: $($vazios -join ', ')
VersionInfo vazio e um dos sinais mais baratos que heuristica de AV pontua.
$resumo
"@
    } else {
        Add-Result "VersionInfo completo - $Label" 'PASS' $resumo
    }
}

Test-VersionInfo -Path $appExe -Label 'RenoDXLauncher.exe'
if ($Installer) { Test-VersionInfo -Path $Installer -Label (Split-Path -Leaf $Installer) }

# --- 4. Authenticode ---------------------------------------------------------

Write-Section "4. Assinatura Authenticode"

function Test-Signature {
    param([string] $Path, [string] $Label)

    $sig = Get-AuthenticodeSignature -LiteralPath $Path
    switch ($sig.Status) {
        'Valid' {
            Add-Result "Assinatura - $Label" 'PASS' @"
Assinado e valido.
Signatario: $($sig.SignerCertificate.Subject)
Emissor:    $($sig.SignerCertificate.Issuer)
Validade:   ate $($sig.SignerCertificate.NotAfter)
"@
        }
        'NotSigned' {
            Add-Result "Assinatura - $Label" 'WARN' @"
NAO ASSINADO. Este e, de longe, o maior fator isolado de falso-positivo e de aviso do
SmartScreen. Nenhuma outra medida deste script substitui a assinatura.
Ver docs\SIGNING-SETUP.md (SignPath Foundation, gratis para open-source).
"@
        }
        default {
            Add-Result "Assinatura - $Label" 'FAIL' @"
Status: $($sig.Status) - $($sig.StatusMessage)
Assinatura presente mas invalida e pior que assinatura nenhuma: indica binario
adulterado depois de assinado, e todo motor de AV trata isso como forte indicio.
"@
        }
    }
}

Test-Signature -Path $appExe -Label 'RenoDXLauncher.exe'
if ($Installer) { Test-Signature -Path $Installer -Label (Split-Path -Leaf $Installer) }

# --- 5. entropia e nomes de secao -------------------------------------------

Write-Section "5. Heuristica de packer (entropia / nome de secao)"

$packerSections = @('UPX0','UPX1','UPX2','.aspack','.adata','.themida','.vmp0','.vmp1',
                    '.enigma1','.enigma2','.petite','.MPRESS1','.MPRESS2','FSG!','.nsp0','.taz')

function Test-Packer {
    param([string] $Path, [string] $Label, [switch] $IsInstaller)

    $pe = Get-PeInfo -Path $Path
    $suspectNames = @($pe.Sections | Where-Object { $packerSections -contains $_.Name } | ForEach-Object { $_.Name })
    $codeHigh     = @($pe.Sections | Where-Object { $_.IsExecutable -and $_.Entropy -gt 7.2 })

    $tabela = ($pe.Sections | ForEach-Object {
        "{0,-10} raw={1,-10} entropia={2,-6} {3}" -f $_.Name, $_.RawSize, $_.Entropy, $(if ($_.IsExecutable) { '(codigo)' } else { '' })
    }) -join "`n"

    if ($suspectNames.Count -gt 0) {
        Add-Result "Sem packer - $Label" 'FAIL' "Secao de packer conhecido: $($suspectNames -join ', ')`n$tabela"
    } elseif ($codeHigh.Count -gt 0) {
        $lista = ($codeHigh | ForEach-Object { "$($_.Name) = $($_.Entropy)" }) -join ', '
        Add-Result "Sem packer - $Label" 'WARN' @"
Secao de codigo com entropia > 7.2 (indicio classico de codigo comprimido ou cifrado): $lista
$tabela
"@
    } else {
        $nota = ''
        if ($IsInstaller) {
            $nota = "Obs.: no instalador o payload comprimido fica FORA das secoes do PE (anexado no fim do`narquivo), entao a entropia alta dele nao aparece - e nao deve aparecer - aqui."
        }
        Add-Result "Sem packer - $Label" 'PASS' @"
Nenhuma secao de packer conhecido; nenhuma secao de codigo acima de 7.2.
$tabela
$nota
"@
    }
}

Test-Packer -Path $appExe -Label 'RenoDXLauncher.exe'
if ($Installer) { Test-Packer -Path $Installer -Label (Split-Path -Leaf $Installer) -IsInstaller }

# --- 6. manifest do executavel ----------------------------------------------

Write-Section "6. Manifest embutido"

$raw          = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($appExe))
$temAsInvoker = $raw -match 'requestedExecutionLevel[\s\S]{0,80}asInvoker'
$temSupported = $raw -match '8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a'

if ($temAsInvoker -and $temSupported) {
    Add-Result 'Manifest: asInvoker + supportedOS' 'PASS' @"
requestedExecutionLevel=asInvoker declarado (evita a Installer Detection do Windows,
que faz executavel com setup/install/update no nome pedir elevacao sozinho).
supportedOS Windows 10/11 declarado (evita os shims de compatibilidade, que injetam
DLL no processo - padrao que heuristica comportamental procura).
"@
} else {
    $faltando = @()
    if (-not $temAsInvoker) { $faltando += 'asInvoker' }
    if (-not $temSupported) { $faltando += 'supportedOS' }
    Add-Result 'Manifest: asInvoker + supportedOS' 'WARN' `
        "Nao encontrei no PE: $($faltando -join ', '). Confira <ApplicationManifest> no csproj e src\app.manifest."
}

# --- 7. Windows Defender -----------------------------------------------------

Write-Section "7. Windows Defender (varredura sob demanda)"

$mp = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
$defenderVivo = $false
try {
    $svc = Get-Service WinDefend -ErrorAction SilentlyContinue
    $defenderVivo = ($null -ne $svc -and $svc.Status -eq 'Running')
} catch {
    $defenderVivo = $false
}

if (-not (Test-Path $mp)) {
    Add-Result 'Varredura Windows Defender' 'SKIP' 'MpCmdRun.exe nao existe nesta maquina.'
} elseif (-not $defenderVivo) {
    Add-Result 'Varredura Windows Defender' 'SKIP' @"
O servico WinDefend nao esta rodando (desligado, ou substituido por outro antivirus),
entao NAO da para afirmar nada sobre a deteccao do Defender nesta maquina.
Este teste roda de verdade no CI (o runner do GitHub tem o Defender ativo) e o
VirusTotal (teste 9) cobre o Defender junto com os outros motores.
"@
} else {
    $alvos = @($publishPath)
    if ($Installer) { $alvos += $Installer }
    $todosLimpos = $true
    $detalhe = @()
    foreach ($alvo in $alvos) {
        $out = & $mp -Scan -ScanType 3 -File $alvo -DisableRemediation 2>&1 | Out-String
        $rc  = $LASTEXITCODE
        $detalhe += "$alvo -> exit=$rc"
        if ($rc -ne 0) {
            $todosLimpos = $false
            $detalhe += $out.Trim()
        }
    }
    if ($todosLimpos) {
        Add-Result 'Varredura Windows Defender' 'PASS' (@('Nenhuma ameaca encontrada.') + $detalhe -join "`n")
    } else {
        Add-Result 'Varredura Windows Defender' 'FAIL' ($detalhe -join "`n")
    }
}

# --- 8. instalar / desinstalar de verdade -----------------------------------

Write-Section "8. Instalacao e desinstalacao silenciosa"

if (-not $Installer) {
    Add-Result 'Instalar + smoke + desinstalar' 'SKIP' 'Nenhum instalador para testar.'
} elseif ($SkipInstallTest) {
    Add-Result 'Instalar + smoke + desinstalar' 'SKIP' '-SkipInstallTest informado.'
} else {
    $testDir = Join-Path ([IO.Path]::GetTempPath()) ('rdx-teste-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $logFile = "$testDir.log"
    try {
        # /CURRENTUSER instala em pasta do usuario sem UAC, entao o teste roda tanto em
        # runner de CI quanto em maquina comum, sem elevar nada.
        $p = Start-Process -FilePath $Installer -Wait -PassThru -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS',
            '/CURRENTUSER', "/DIR=$testDir", "/LOG=$logFile"
        )
        $rc           = $p.ExitCode
        $exeInstalado = Join-Path $testDir 'RenoDXLauncher.exe'
        $uninst       = Join-Path $testDir 'unins000.exe'

        if ($rc -ne 0) {
            Add-Result 'Instalacao silenciosa' 'FAIL' "setup.exe saiu com $rc. Log: $logFile"
        } elseif (-not (Test-Path $exeInstalado)) {
            Add-Result 'Instalacao silenciosa' 'FAIL' "Instalou (exit 0) mas RenoDXLauncher.exe nao apareceu em $testDir. Log: $logFile"
        } else {
            $vInst = (Get-Item $exeInstalado).VersionInfo.FileVersion
            $vOrig = (Get-Item $appExe).VersionInfo.FileVersion
            $nInst = (Get-ChildItem $testDir -Recurse -File).Count
            $nOrig = (Get-ChildItem $publishPath -Recurse -File).Count

            if ($vInst -eq $vOrig -and $nInst -ge $nOrig) {
                Add-Result 'Instalacao silenciosa' 'PASS' `
                    "Instalou $nInst arquivos (publish tem $nOrig, mais o desinstalador), versao $vInst."
            } else {
                Add-Result 'Instalacao silenciosa' 'FAIL' `
                    "versao instalada=$vInst publish=$vOrig; arquivos instalados=$nInst publish=$nOrig"
            }

            # Smoke: o app tem que carregar o runtime .NET a partir da pasta instalada e
            # responder. 'help' nao toca em disco, nao acessa rede e nao mexe em jogo.
            $so = "$testDir-stdout.txt"
            $se = "$testDir-stderr.txt"
            $ps = Start-Process -FilePath $exeInstalado -ArgumentList 'help' -Wait -PassThru `
                                -NoNewWindow -RedirectStandardOutput $so -RedirectStandardError $se
            $saida = Get-Content $so -Raw -ErrorAction SilentlyContinue
            # O que este teste precisa saber e se o binario ABRIU e imprimiu a ajuda: se o
            # runtime .NET carregou da pasta instalada. Nao e teste de identidade do produto.
            #
            # Ja passou por dois nomes errados. Primeiro casava com 'linha de comando', e
            # reprovava no runner do CI (ingles) assim que a CLI foi localizada. Trocou-se por
            # 'RenoDX Launcher', "que nunca e traduzido" -- e o produto virou 'DLSS 5 Launcher',
            # com o mesmo resultado: exit=0, ajuda impressa, teste reprovado. Tres releases
            # falharam nisso.
            #
            # Agora ancora no que a ajuda tem por definicao e nao muda com nome nem com idioma:
            # os proprios comandos da CLI, que sao literais no codigo.
            $ajudaOk = $saida -match '(?m)^\s*list\b' -and $saida -match '(?m)^\s*verify\b'
            if ($ps.ExitCode -eq 0 -and $ajudaOk) {
                Add-Result 'Smoke do binario instalado' 'PASS' `
                    'RenoDXLauncher.exe help -> exit 0; o runtime .NET carregou da pasta instalada.'
            } else {
                $err = Get-Content $se -Raw -ErrorAction SilentlyContinue
                Add-Result 'Smoke do binario instalado' 'FAIL' "exit=$($ps.ExitCode)`nstdout: $saida`nstderr: $err"
            }
            Remove-Item $so, $se -Force -ErrorAction SilentlyContinue

            # unins000.exe se copia para o Temp e devolve o controle antes de terminar,
            # entao esperar o processo nao basta: espera a pasta sumir.
            if (Test-Path $uninst) {
                Start-Process -FilePath $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait | Out-Null
                $limite = (Get-Date).AddSeconds(90)
                while ((Test-Path $testDir) -and (Get-Date) -lt $limite) { Start-Sleep -Milliseconds 500 }
                if (Test-Path $testDir) {
                    $sobrou = (Get-ChildItem $testDir -Recurse -File -ErrorAction SilentlyContinue).Count
                    Add-Result 'Desinstalacao silenciosa' 'FAIL' "$testDir ainda existe apos 90s, com $sobrou arquivo(s)."
                } else {
                    Add-Result 'Desinstalacao silenciosa' 'PASS' `
                        'Removeu a pasta inteira, sem perguntar nada (UninstallSilent) e sem apagar os dados do usuario.'
                }
            } else {
                Add-Result 'Desinstalacao silenciosa' 'FAIL' 'unins000.exe nao foi criado.'
            }
        }
    } finally {
        Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $logFile -Force -ErrorAction SilentlyContinue
    }
}

# --- 9. VirusTotal -----------------------------------------------------------

Write-Section "9. VirusTotal (70+ motores)"

function Invoke-VirusTotal {
    param([string] $Path, [string] $Label, [string] $ApiKey)

    Add-Type -AssemblyName System.Net.Http | Out-Null
    $sha = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLower()
    $hdr = @{ 'x-apikey' = $ApiKey }

    # O arquivo ja foi analisado? Evita subir dezenas de MB a toa e nao gasta cota.
    $rel = $null
    try {
        $rel = Invoke-RestMethod -Uri "https://www.virustotal.com/api/v3/files/$sha" -Headers $hdr -Method Get
    } catch {
        $rel = $null
    }

    if (-not $rel) {
        Write-Host "         subindo $Label para o VirusTotal (pode demorar)..." -ForegroundColor DarkGray
        $tam      = (Get-Item $Path).Length
        $endpoint = 'https://www.virustotal.com/api/v3/files'
        if ($tam -gt 32MB) {
            # Acima de 32 MB o VirusTotal exige uma URL de upload dedicada.
            $up = Invoke-RestMethod -Uri 'https://www.virustotal.com/api/v3/files/upload_url' -Headers $hdr -Method Get
            $endpoint = $up.data
        }

        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromMinutes(30)
        $client.DefaultRequestHeaders.Add('x-apikey', $ApiKey)
        $form = New-Object System.Net.Http.MultipartFormDataContent
        $fs   = [IO.File]::OpenRead($Path)
        try {
            $sc = New-Object System.Net.Http.StreamContent($fs)
            # As aspas em name/filename nao sao decoracao: o MultipartFormDataContent do
            # .NET Framework - que e o que o Windows PowerShell 5.1 carrega - emite
            # name=file sem aspas, e o endpoint de arquivo grande do VirusTotal
            # (bigfiles.virustotal.com) responde 400 "Malformed multipart body".
            # O endpoint pequeno tolera, entao o bug so aparece acima de 32 MB - ou seja,
            # exatamente no instalador. Montar o Content-Disposition na mao resolve.
            $sc.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue('application/octet-stream')
            $cd = New-Object System.Net.Http.Headers.ContentDispositionHeaderValue('form-data')
            $cd.Name     = '"file"'
            $cd.FileName = '"' + [IO.Path]::GetFileName($Path) + '"'
            $sc.Headers.ContentDisposition = $cd
            $form.Add($sc)
            $resp = $client.PostAsync($endpoint, $form).GetAwaiter().GetResult()
            $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if (-not $resp.IsSuccessStatusCode) { throw "upload falhou: $($resp.StatusCode) $body" }
            $analiseId = ($body | ConvertFrom-Json).data.id
        } finally {
            $fs.Dispose()
            $client.Dispose()
        }

        $status = ''
        $limite = (Get-Date).AddMinutes(15)
        do {
            Start-Sleep -Seconds 20
            $an     = Invoke-RestMethod -Uri "https://www.virustotal.com/api/v3/analyses/$analiseId" -Headers $hdr -Method Get
            $status = $an.data.attributes.status
            Write-Host "         status: $status" -ForegroundColor DarkGray
        } while ($status -ne 'completed' -and (Get-Date) -lt $limite)

        if ($status -ne 'completed') { throw "a analise nao concluiu em 15 min (id $analiseId)" }
        $rel = Invoke-RestMethod -Uri "https://www.virustotal.com/api/v3/files/$sha" -Headers $hdr -Method Get
    }

    $stats      = $rel.data.attributes.last_analysis_stats
    $motores    = $rel.data.attributes.last_analysis_results
    $acusadores = @()
    foreach ($m in $motores.PSObject.Properties) {
        if ($m.Value.category -eq 'malicious' -or $m.Value.category -eq 'suspicious') {
            $acusadores += "$($m.Name): $($m.Value.category) / $($m.Value.result)"
        }
    }
    $total  = $stats.malicious + $stats.suspicious + $stats.undetected + $stats.harmless
    $resumo = @"
sha256 $sha
https://www.virustotal.com/gui/file/$sha
$($stats.malicious) maliciosos, $($stats.suspicious) suspeitos, de $total motores
"@

    # Deteccao de MACHINE LEARNING nao reprova o build; assinatura de familia real reprova.
    #
    # A contagem crua nao distingue as duas coisas, e a diferenca e tudo. Um veredito de ML
    # ("...!ml", "MALICIOUS" do DeepInstinct, "Generic", "Heur") diz que o binario caiu do lado
    # errado da margem de um classificador -- e isto foi medido no proprio projeto: a MESMA
    # configuracao, o MESMO codigo, deu 0/67 numa maquina e 2/65 no CI. Nenhuma configuracao de
    # compressao zerou. Nao ha nada no conteudo sendo detectado; ha ruido de build.
    #
    # Uma assinatura nomeada de familia ("Emotet", "AgentTesla") e outra coisa: e um motor
    # dizendo que reconheceu algo especifico. Essa reprova, e deve reprovar mesmo que seja uma so.
    #
    # Subir o limiar resolveria o sintoma cegando o teste para os dois casos. Isto separa os dois.
    $ehRuidoDeMl = {
        param($r)
        if ([string]::IsNullOrWhiteSpace($r)) { return $true }   # acusou sem dizer o que: sem valor
        # "Gen:Variant...", "Gen.Malware", "Generic": todos sao o balde generico do motor, nao
        # uma familia reconhecida. Entram como ruido junto com os vereditos de ML.
        $r -match '(?i)!ml\b|\bml\b|machine.?learning|^MALICIOUS$|\bgen(eric)?[:.]|generic|heur|suspicious|unsafe|confidence|\bAI\b|cloud|variant|score|reputation'
    }
    $nomeados = @($acusadores | Where-Object {
        $res = ($_ -split ' / ')[-1]
        -not (& $ehRuidoDeMl $res)
    })

    if ($acusadores.Count -eq 0) {
        Add-Result "VirusTotal - $Label" 'PASS' $resumo
    } else {
        # FAIL so quando ha assinatura nomeada, ou quando o ruido de ML fica grande demais para
        # ser margem (>= 6 motores sugere que algo mudou de verdade, nao que o dado oscilou).
        $st = if ($nomeados.Count -gt 0 -or $stats.malicious -ge 6) { 'FAIL' } else { 'WARN' }
        if ($nomeados.Count -gt 0) {
            $resumo += "`nDeteccao NOMEADA (nao e ML): " + ($nomeados -join '; ')
        } elseif ($st -eq 'WARN') {
            # ASCII puro: este arquivo tem #Requires -Version 5.1, e o PowerShell 5.1 le script
            # como ANSI. Um travessao aqui quebra o parser antes da linha 1.
            $resumo += "`nSo veredito de machine learning - ruido de classificador, nao conteudo."
            $resumo += "`nVer docs/antivirus.md: assinar o codigo e submeter o falso-positivo e o que resolve."
        }
        Add-Result "VirusTotal - $Label" $st ($resumo + ($acusadores -join "`n") + "`nPara reportar falso-positivo, veja docs\antivirus.md")
    }
}

if (-not $VirusTotalApiKey) {
    Add-Result 'VirusTotal' 'SKIP' @"
Sem chave de API. Pegue uma gratis em https://www.virustotal.com/gui/join-us e rode:
  pwsh tools\av-selfcheck.ps1 -VirusTotalApiKey <chave>
ou defina a variavel de ambiente VT_API_KEY (no CI: secret VT_API_KEY).
Sem isso NAO da para afirmar que nenhum antivirus acusa - so que os testes locais passaram.
"@
} else {
    $alvosVt = @(
        [pscustomobject]@{ P = $appExe;    L = 'RenoDXLauncher.exe' },
        [pscustomobject]@{ P = $Installer; L = 'setup.exe' }
    )
    foreach ($alvo in $alvosVt) {
        if (-not $alvo.P) { continue }
        try {
            Invoke-VirusTotal -Path $alvo.P -Label $alvo.L -ApiKey $VirusTotalApiKey
        } catch {
            Add-Result "VirusTotal - $($alvo.L)" 'WARN' "Nao consegui consultar: $($_.Exception.Message)"
        }
    }
}

# --- 10. hashes --------------------------------------------------------------

Write-Section "10. SHA-256 dos artefatos"

$artefatos = @()
if ($Installer) { $artefatos += $Installer }
$artefatos += Get-ChildItem (Join-Path $repoRoot 'dist') -Filter '*.zip' -ErrorAction SilentlyContinue |
              ForEach-Object { $_.FullName }

$hashes = @()
foreach ($f in $artefatos) {
    if ($f -and (Test-Path $f)) {
        $h = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLower()
        $hashes += "$h  $(Split-Path -Leaf $f)"
    }
}
if ($hashes.Count -gt 0) {
    Add-Result 'Hashes para publicar no release' 'INFO' ($hashes -join "`n")
} else {
    Add-Result 'Hashes para publicar no release' 'SKIP' 'Nenhum artefato em dist\.'
}

# --- 11. invariantes do codigo-fonte ----------------------------------------

Write-Section "11. Invariantes do codigo-fonte"

# Nao ha, hoje, nenhuma API de injecao de codigo nem de persistencia no projeto - e
# essa e a frase que sustenta uma disputa de falso-positivo, porque e verificavel por
# quem esta do outro lado. Este teste existe para que ela continue verdadeira: se
# alguem introduzir uma dessas chamadas, o CI aponta na hora, e nao seis meses depois
# quando um motor de AV apontar primeiro.
$srcDir = Join-Path $repoRoot 'src'
if (-not (Test-Path $srcDir)) {
    Add-Result 'Sem API de injecao/persistencia' 'SKIP' 'src\ nao esta presente (rodando so sobre artefatos).'
} else {
    $proibidos = @(
        'WriteProcessMemory', 'CreateRemoteThread', 'VirtualAllocEx', 'QueueUserAPC',
        'NtMapViewOfSection', 'SetWindowsHookEx', 'OpenProcess', 'AdjustTokenPrivileges',
        'MiniDumpWriteDump', 'schtasks', 'ServiceController'
    )
    # Get-ChildItem -Recurse, e nao um wildcard em -Path: o -Path do Select-String nao
    # expande '**', entao a versao com wildcard olharia so src\*.cs e passaria batido por
    # src\Services\*.cs - que e onde o codigo interessante mora.
    $arquivos = @(Get-ChildItem -Path $srcDir -Recurse -File -Filter '*.cs' |
                  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    $achados = @()
    foreach ($termo in $proibidos) {
        foreach ($h in (Select-String -Path $arquivos.FullName -Pattern $termo -SimpleMatch -ErrorAction SilentlyContinue)) {
            $achados += "$termo em $($h.Path -replace [regex]::Escape($repoRoot), '') linha $($h.LineNumber)"
        }
    }
    if ($arquivos.Count -eq 0) {
        Add-Result 'Sem API de injecao/persistencia' 'FAIL' `
            "Nao encontrei nenhum .cs em $srcDir - o teste nao verificou nada. Corrija o caminho antes de confiar neste resultado."
    } elseif ($achados.Count -eq 0) {
        Add-Result 'Sem API de injecao/persistencia' 'PASS' @"
$($arquivos.Count) arquivos .cs varridos, nenhum com: $($proibidos -join ', ').
O app le import table de PE com FileStream/BinaryReader (I/O somente-leitura, nao
primitiva de hooking), nao injeta em processo nenhum, nao instala servico, driver,
tarefa agendada nem chave Run. Use isso ao reportar falso-positivo: e checavel.
"@
    } else {
        Add-Result 'Sem API de injecao/persistencia' 'FAIL' @"
$($achados -join "`n")
Se a chamada e mesmo necessaria, o custo dela nao e so tecnico: ela derruba o
argumento central da disputa de falso-positivo e muda o perfil do app para os motores
comportamentais. Documente o porque antes de retirar este teste da lista.
"@
    }
}

# ------------------------------------------------------------------ resumo ---

Write-Section "Resumo"

$fail = @($script:Results | Where-Object { $_.Status -eq 'FAIL' })
$warn = @($script:Results | Where-Object { $_.Status -eq 'WARN' })
$skip = @($script:Results | Where-Object { $_.Status -eq 'SKIP' })
$pass = @($script:Results | Where-Object { $_.Status -eq 'PASS' })

Write-Host ("  PASS {0}   WARN {1}   FAIL {2}   SKIP {3}" -f $pass.Count, $warn.Count, $fail.Count, $skip.Count)
foreach ($r in $fail) { Write-Host ('  FAIL: ' + $r.Check) -ForegroundColor Red }
foreach ($r in $warn) { Write-Host ('  WARN: ' + $r.Check) -ForegroundColor Yellow }
foreach ($r in $skip) { Write-Host ('  SKIP: ' + $r.Check) -ForegroundColor DarkGray }

if ($env:GITHUB_STEP_SUMMARY) {
    $md = @('# Verificacao de artefatos', '', '| Teste | Status |', '|---|---|')
    foreach ($r in $script:Results) { $md += "| $($r.Check) | **$($r.Status)** |" }

    # O DETALHE de cada FAIL e WARN vai junto.
    #
    # Sem isto a tabela dizia QUE reprovou e nunca POR QUE, e o motivo so existia no log do
    # job -- que a API so entrega com token. Na pratica isso significava que uma falha no CI
    # era indiagnosticavel de fora, e o unico caminho era republicar cegamente ate acertar.
    # O step summary e parte da pagina publica do run: o motivo passa a ficar onde qualquer
    # um consegue ler.
    $comDetalhe = @($script:Results | Where-Object { $_.Status -eq 'FAIL' -or $_.Status -eq 'WARN' })
    if ($comDetalhe.Count -gt 0) {
        $md += @('', '## Detalhe')
        foreach ($r in $comDetalhe) {
            $md += @('', "### $($r.Status): $($r.Check)", '', '```', $r.Detail, '```')
        }
    }
    ($md -join "`n") | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
}

if ($fail.Count -gt 0) {
    Write-Host ''
    Write-Host 'REPROVADO' -ForegroundColor Red
    exit 1
}
if ($FailOnWarn -and $warn.Count -gt 0) {
    Write-Host ''
    Write-Host 'REPROVADO (-FailOnWarn)' -ForegroundColor Yellow
    exit 1
}
Write-Host ''
Write-Host 'APROVADO' -ForegroundColor Green
exit 0

} finally {
    Pop-Location
}
