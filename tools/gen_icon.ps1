# Compatibility entry point. All branding is generated from gen_brand.ps1.
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'gen_brand.ps1')
