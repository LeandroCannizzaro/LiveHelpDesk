<#
.SYNOPSIS
  Installa LiveHelpDesk copiando i file del pacchetto (cartella "app" accanto a questo script).
.DESCRIPTION
  Per utente (predefinito, nessun amministratore): %LocalAppData%\Programs\LiveHelpDesk.
  Per macchina (-AllUsers, richiede amministratore): %ProgramFiles%\LiveHelpDesk; con -InstallService registra anche il servizio Windows.
  Non tocca mai i dati (%ProgramData%\LiveHelpDesk, %LocalAppData%\LiveHelpDesk) ne' config.json esistente.
.PARAMETER AllUsers
  Installa per tutti gli utenti (amministratore).
.PARAMETER InstallService
  Con -AllUsers: registra e avvia il servizio "LiveHelpDesk" (monitor attivo anche senza utente connesso).
.PARAMETER Autostart
  Avvia LiveHelpDesk all'accesso dell'utente (per utente: HKCU\Run; con -AllUsers: HKLM\Run, tutti gli utenti).
.PARAMETER NoStart
  Non avviare l'applicazione al termine.
#>
[CmdletBinding()]
param(
    [switch]$AllUsers,
    [switch]$InstallService,
    [switch]$Autostart,
    [string]$InstallDir,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
if (-not (Test-Path (Join-Path $source 'LiveHelpDesk.exe'))) { throw "Pacchetto non valido: manca $source\LiveHelpDesk.exe" }

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (($AllUsers -or $InstallService) -and -not $isAdmin) { throw 'Per -AllUsers e -InstallService servono privilegi di amministratore (usare "Esegui come amministratore").' }
if ($InstallService -and -not $AllUsers) { throw '-InstallService richiede -AllUsers.' }

if (-not $InstallDir) {
    $InstallDir = if ($AllUsers) { Join-Path $env:ProgramFiles 'LiveHelpDesk' } else { Join-Path $env:LOCALAPPDATA 'Programs\LiveHelpDesk' }
}
$exe = Join-Path $InstallDir 'LiveHelpDesk.exe'
$serviceExe = Join-Path $InstallDir 'service\LiveHelpDesk.Service.exe'

# WebView2: la dashboard non si apre senza (e` gia` presente con Windows 11 / Edge / Office).
$wv2 = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
if (-not ($wv2 | Where-Object { (Get-ItemProperty $_ -ErrorAction SilentlyContinue).pv })) {
    Write-Warning 'WebView2 Runtime non trovato: installarlo da https://go.microsoft.com/fwlink/p/?LinkId=2124703 (la dashboard non si aprira'' senza).'
}

# Chiude cio' che e' in esecuzione da questa cartella, ricordando se il servizio girava.
$serviceWasRunning = $false
$service = Get-Service -Name LiveHelpDesk -ErrorAction SilentlyContinue
if ($service -and $isAdmin) {
    $serviceWasRunning = $service.Status -eq 'Running'
    if ($serviceWasRunning) { Write-Host 'Arresto il servizio...'; Stop-Service LiveHelpDesk -Force }
}
Get-Process -Name 'LiveHelpDesk', 'LiveHelpDesk.Service' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Write-Host "Chiudo $($_.Name) ($($_.Id))..."; $_ | Stop-Process -Force; $_.WaitForExit(10000) | Out-Null }

Write-Host "Copio i file in $InstallDir ..."
New-Item $InstallDir -ItemType Directory -Force | Out-Null
robocopy $source $InstallDir /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:2 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copia fallita (robocopy $LASTEXITCODE)" }

# Scorciatoia nel menu Start.
$programs = [Environment]::GetFolderPath($(if ($AllUsers) { 'CommonPrograms' } else { 'Programs' }))
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $programs 'LiveHelpDesk.lnk'))
$link.TargetPath = $exe
$link.WorkingDirectory = $InstallDir
$link.Description = 'LiveHelpDesk'
$link.Save()

$runKey = if ($AllUsers) { 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' }
if ($Autostart) { Set-ItemProperty $runKey -Name LiveHelpDesk -Value "`"$exe`" --minimized" }

# Voce in "App installate" per la disinstallazione da Impostazioni.
$uninstallKey = if ($AllUsers) { 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LiveHelpDesk' } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveHelpDesk' }
$version = (Get-Item $exe).VersionInfo.ProductVersion
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $InstallDir -Force
New-Item $uninstallKey -Force | Out-Null
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'uninstall.ps1')`"" + $(if ($AllUsers) { ' -AllUsers' } else { '' })
Set-ItemProperty $uninstallKey -Name DisplayName -Value 'LiveHelpDesk'
Set-ItemProperty $uninstallKey -Name DisplayVersion -Value $version
Set-ItemProperty $uninstallKey -Name Publisher -Value 'Leandro Cannizzaro'
Set-ItemProperty $uninstallKey -Name InstallLocation -Value $InstallDir
Set-ItemProperty $uninstallKey -Name UninstallString -Value $uninstallCmd
Set-ItemProperty $uninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty $uninstallKey -Name NoRepair -Value 1 -Type DWord

if ($InstallService) {
    Write-Host 'Registro il servizio...'
    & $serviceExe uninstall | Out-Null   # idempotente: aggiorna anche il percorso se e' cambiato
    & $serviceExe install
    if ($LASTEXITCODE) { throw "Installazione del servizio fallita ($LASTEXITCODE)" }
}
elseif ($serviceWasRunning) {
    Write-Host 'Riavvio il servizio...'
    Start-Service LiveHelpDesk
}

Write-Host "LiveHelpDesk $version installato in $InstallDir"
if (-not $NoStart -and -not $InstallService) { Start-Process $exe }
