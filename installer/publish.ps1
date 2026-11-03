<#
.SYNOPSIS
  Pubblica app + servizio self-contained (win-x64) in <Output>\app (servizio in app\service). Usato da build-msi.ps1.
.EXAMPLE
  .\installer\publish.ps1
  .\installer\publish.ps1 -Output C:\temp\package
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\package'),
    [string]$Configuration = 'Release',
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Output = [IO.Path]::GetFullPath($Output)
$app = Join-Path $Output 'app'

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item $app -ItemType Directory | Out-Null

$common = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', '-p:DebugType=none', '-p:DebugSymbols=false', '--nologo', '-v', 'q')
if ($Version) { $common += "-p:Version=$Version" }

# Il servizio vive in service\ accanto all'app: e` dove ServiceExecutable.Find lo cerca.
dotnet publish (Join-Path $root 'src\LiveHelpDesk.App') @common -o $app
if ($LASTEXITCODE) { throw 'publish dell''app fallito' }
dotnet publish (Join-Path $root 'src\LiveHelpDesk.Service') @common -o (Join-Path $app 'service')
if ($LASTEXITCODE) { throw 'publish del servizio fallito' }

# DebugType=none non copre i simboli dei pacchetti nativi (SkiaSharp, HarfBuzz: ~100 MB).
Get-ChildItem $app -Recurse -Filter *.pdb | Remove-Item -Force

$size = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Pacchetto pronto in {0} ({1:N0} MB)" -f $Output, $size)
