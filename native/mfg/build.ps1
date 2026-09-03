# Compila o add-on de Multi Frame Generation e o poe onde o launcher o embute.
#
# Nao roda na integracao continua: ela nao tem o SDK do Streamline, e o SDK nao e redistribuivel
# neste repositorio. Ver README.md.
param(
    [Parameter(Mandatory = $true)]
    [string] $StreamlineRoot,
    [string] $BuildDir = "$PSScriptRoot\build"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path "$StreamlineRoot\include\sl_dlss_g.h")) {
    throw "StreamlineRoot invalido: falta include\sl_dlss_g.h em $StreamlineRoot"
}

# O CMake do Build Tools serve; o do PATH tambem, quando existe.
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $cmake = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
}
if (-not (Test-Path $cmake)) { throw "cmake nao encontrado" }

& $cmake -S "$PSScriptRoot\source" -B $BuildDir -G "Visual Studio 17 2022" -A x64 `
    -DSTREAMLINE_ROOT="$StreamlineRoot"
if ($LASTEXITCODE -ne 0) { throw "cmake configure falhou" }

& $cmake --build $BuildDir --config Release --parallel
if ($LASTEXITCODE -ne 0) { throw "cmake build falhou" }

$saida = Join-Path $BuildDir 'Release\renodx-mfg.addon64'
if (-not (Test-Path $saida)) { throw "build nao produziu $saida" }

$destino = Join-Path $PSScriptRoot '..\..\src\Assets\mfg\renodx-mfg.addon64'
New-Item -ItemType Directory -Force -Path (Split-Path $destino) | Out-Null
Copy-Item $saida $destino -Force

$hash = (Get-FileHash $destino -Algorithm SHA256).Hash
$tamanho = (Get-Item $destino).Length

Write-Output ''
Write-Output "add-on: $((Resolve-Path $destino).Path)"
Write-Output "SHA-256: $hash"
Write-Output "bytes  : $tamanho"
Write-Output ''
Write-Output 'Copie os dois para src\Services\MfgService.cs:'
Write-Output "    private const string EmbeddedSha256 = `"$hash`";"
Write-Output "    private const long EmbeddedLength = $tamanho;"
