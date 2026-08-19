#Requires -Version 5.1
<#
.SYNOPSIS
    Publica o app e compila o instalador (Inno Setup) em dist\.

.DESCRIPTION
    Faz exatamente o que o workflow de release faz, para dar para reproduzir o
    artefato oficial na sua maquina e conferir que bate:

      1. dotnet publish self-contained, em PASTA (nunca single-file)
      2. baixa o Inno Setup se nao achar (versao e SHA-256 fixados)
      3. compila installer\RenoDXLauncher.iss -> dist\RenoDXLauncher-<ver>-setup.exe
      4. opcionalmente empacota o zip portable
      5. chama tools\av-selfcheck.ps1

.PARAMETER Version
    Versao de exibicao do instalador. Padrao: a do binario compilado.

.PARAMETER Zip
    Tambem gera o zip portable em dist\.

.PARAMETER SkipCheck
    Nao roda a bateria de verificacao no final.

.EXAMPLE
    pwsh tools\build-installer.ps1
.EXAMPLE
    pwsh tools\build-installer.ps1 -Version 1.11.3 -Zip
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $Zip,
    [switch] $SkipCheck
)

$ErrorActionPreference = 'Stop'

# Versao do Inno Setup fixada, com hash conferido: o instalador oficial e baixado da
# release do proprio jrsoftware no GitHub, nao de espelho de terceiro.
$InnoVersion = '6.7.3'
$InnoUrl     = "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-$InnoVersion.exe"
$InnoSha256  = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {

$publishDir = Join-Path $repoRoot 'publish'
$distDir    = Join-Path $repoRoot 'dist'

# --- 1. publish --------------------------------------------------------------

Write-Host '==> dotnet publish (self-contained, em pasta)' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# PublishSingleFile=false e o ponto que mais importa aqui: um exe que se auto-extrai
# em disco antes de rodar e o padrao que heuristica de antivirus mais pontua.
& dotnet publish (Join-Path $repoRoot 'src\RenoDXLauncher.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
    -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou ($LASTEXITCODE)" }

$appExe = Join-Path $publishDir 'RenoDXLauncher.exe'
if (-not (Test-Path $appExe)) { throw "publish nao gerou $appExe" }
if (-not $Version) { $Version = (Get-Item $appExe).VersionInfo.ProductVersion -replace '\+.*$', '' }
Write-Host "    versao: $Version" -ForegroundColor DarkGray

# --- 2. Inno Setup -----------------------------------------------------------

function Resolve-Iscc {
    if ($env:ISCC_PATH -and (Test-Path $env:ISCC_PATH)) { return $env:ISCC_PATH }
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )) {
        if (Test-Path $p) { return $p }
    }

    # Nao achou instalado: baixa numa pasta de cache e instala em modo portatil,
    # sem tocar em registro nem em Program Files.
    $cache = Join-Path ([IO.Path]::GetTempPath()) "innosetup-$InnoVersion"
    $iscc  = Join-Path $cache 'ISCC.exe'
    if (Test-Path $iscc) { return $iscc }

    Write-Host "==> baixando Inno Setup $InnoVersion" -ForegroundColor Cyan
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "innosetup-$InnoVersion.exe"
    if (-not (Test-Path $tmp)) {
        $old = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try { Invoke-WebRequest -Uri $InnoUrl -OutFile $tmp -UseBasicParsing }
        finally { $ProgressPreference = $old }
    }
    $sha = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLower()
    if ($sha -ne $InnoSha256) {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        throw "SHA-256 do Inno Setup nao confere.`n  esperado: $InnoSha256`n  obtido:   $sha"
    }

    & $tmp /PORTABLE=1 /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- "/DIR=$cache" | Out-Null
    if (-not (Test-Path $iscc)) { throw "instalacao portatil do Inno Setup falhou em $cache" }
    return $iscc
}

$iscc = Resolve-Iscc
Write-Host "==> ISCC: $iscc" -ForegroundColor DarkGray

# --- 3. compilar o instalador ------------------------------------------------

Write-Host '==> compilando o instalador' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

& $iscc (Join-Path $repoRoot 'installer\RenoDXLauncher.iss') `
    "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$distDir" /Q
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou ($LASTEXITCODE)" }

$setup = Join-Path $distDir "RenoDXLauncher-$Version-setup.exe"
if (-not (Test-Path $setup)) { throw "o instalador nao apareceu em $setup" }
Write-Host ("    {0}  ({1:N1} MB)" -f (Split-Path -Leaf $setup), ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green

# --- 4. zip portable (opcional) ---------------------------------------------

if ($Zip) {
    Write-Host '==> zip portable' -ForegroundColor Cyan
    $zipPath = Join-Path $distDir "RenoDXLauncher-$Version-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
    Write-Host ("    {0}  ({1:N1} MB)" -f (Split-Path -Leaf $zipPath), ((Get-Item $zipPath).Length / 1MB)) -ForegroundColor Green
}

# --- 5. verificacao ----------------------------------------------------------

if (-not $SkipCheck) {
    Write-Host ''
    & (Join-Path $PSScriptRoot 'av-selfcheck.ps1') -Installer $setup
    exit $LASTEXITCODE
}

} finally {
    Pop-Location
}
