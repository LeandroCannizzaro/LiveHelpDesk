<#
.SYNOPSIS
  Crea lo ZIP portatile di LiveHelpDesk: la cartella "app" pubblicata piu' gli script di installazione.
.DESCRIPTION
  Lo ZIP si estrae in una cartella qualsiasi; dentro si trovano install.cmd/uninstall.cmd (e i .ps1) accanto alla
  cartella "app", esattamente il layout che install.ps1 si aspetta. Utile per la distribuzione da una share di rete
  o per chi non vuole l'MSI.
  Se -Package non viene passato (o non esiste) viene prima eseguito publish.ps1 per generare la cartella "app".
.EXAMPLE
  .\installer\build-zip.ps1
  .\installer\build-zip.ps1 -Version 0.2.0 -Package .\artifacts\package\app -Output .\artifacts
#>
param(
    [string]$Version,
    [string]$Package,
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Output = [IO.Path]::GetFullPath($Output)

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Versione non valida per lo ZIP: '$Version' (serve x.y.z)" }

# Se non e' stata indicata una cartella "app" gia' pubblicata, la si pubblica adesso.
if (-not $Package -or -not (Test-Path (Join-Path $Package 'LiveHelpDesk.exe'))) {
    $packageRoot = Join-Path $root 'artifacts\package'
    & (Join-Path $PSScriptRoot 'publish.ps1') -Output $packageRoot -Configuration $Configuration -Version $Version
    $Package = Join-Path $packageRoot 'app'
}
$Package = [IO.Path]::GetFullPath($Package)
if (-not (Test-Path (Join-Path $Package 'LiveHelpDesk.exe'))) { throw "Pacchetto non valido: manca $Package\LiveHelpDesk.exe" }

# Si prepara un'area di staging con gli script accanto a una copia della cartella "app".
$staging = Join-Path ([IO.Path]::GetTempPath()) ("lhd-zip-" + [Guid]::NewGuid().ToString('N'))
try {
    New-Item $staging -ItemType Directory | Out-Null
    foreach ($script in 'install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd') {
        Copy-Item (Join-Path $root $script) $staging -Force
    }
    Copy-Item $Package (Join-Path $staging 'app') -Recurse -Force
    Copy-Item (Join-Path $PSScriptRoot 'count-installation.ps1') (Join-Path $staging 'app') -Force

    New-Item $Output -ItemType Directory -Force | Out-Null
    $zip = Join-Path $Output "LiveHelpDesk-$Version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("ZIP pronto: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
