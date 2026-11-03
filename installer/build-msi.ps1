<#
.SYNOPSIS
  Costruisce l'MSI (non firmato) di LiveHelpDesk: pubblica app e servizio self-contained x64 e li impacchetta con WiX v5.
.EXAMPLE
  .\installer\build-msi.ps1
  .\installer\build-msi.ps1 -Version 0.2.0 -Output \\server\software\LiveHelpDesk
#>
param(
    [string]$Version,
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Output = [IO.Path]::GetFullPath($Output)

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Versione non valida per l'MSI: '$Version' (serve x.y.z)" }

$package = Join-Path $root 'artifacts\package'
& (Join-Path $PSScriptRoot 'publish.ps1') -Output $package -Configuration $Configuration -Version $Version

dotnet build (Join-Path $PSScriptRoot 'LiveHelpDesk.Setup') -c $Configuration --nologo -v q `
    -p:AppSource="$package\app" -p:ProductVersion=$Version -p:OutDir="$package\msi"
if ($LASTEXITCODE) { throw 'build dell''MSI fallita' }

New-Item $Output -ItemType Directory -Force | Out-Null
$msi = Join-Path $Output "LiveHelpDesk-$Version-x64.msi"
Copy-Item (Join-Path $package 'msi\LiveHelpDesk.msi') $msi -Force
Write-Host ("MSI pronto: {0} ({1:N0} MB)" -f $msi, ((Get-Item $msi).Length / 1MB))
