<#
.SYNOPSIS
  Disinstalla LiveHelpDesk installato con install.ps1 (copia dei file, non l'MSI).
.DESCRIPTION
  Rimuove i file dell'applicazione, la scorciatoia nel menu Start, l'avvio automatico e la voce in "App installate".
  Non tocca mai i dati (%ProgramData%\LiveHelpDesk, %LocalAppData%\LiveHelpDesk) ne' config.json, salvo -RemoveData.
.PARAMETER AllUsers
  Rimuove l'installazione per tutti gli utenti (%ProgramFiles%\LiveHelpDesk, richiede amministratore).
.PARAMETER RemoveData
  Elimina anche i dati salvati (configurazione, regole, canali, profili, log).
#>
[CmdletBinding()]
param(
    [switch]$AllUsers,
    [switch]$RemoveData,
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($AllUsers -and -not $isAdmin) { throw 'Per -AllUsers servono privilegi di amministratore (usare "Esegui come amministratore").' }

if (-not $InstallDir) {
    $InstallDir = if ($AllUsers) { Join-Path $env:ProgramFiles 'LiveHelpDesk' } else { Join-Path $env:LOCALAPPDATA 'Programs\LiveHelpDesk' }
}
$serviceExe = Join-Path $InstallDir 'service\LiveHelpDesk.Service.exe'

# Se c'e' il servizio Windows, fermarlo e rimuoverlo (il suo eseguibile sta per sparire).
$service = Get-Service -Name LiveHelpDesk -ErrorAction SilentlyContinue
if ($service -and $isAdmin) {
    if ($service.Status -eq 'Running') { Write-Host 'Arresto il servizio...'; Stop-Service LiveHelpDesk -Force }
    if (Test-Path $serviceExe) { & $serviceExe uninstall | Out-Null }
}
elseif ($service -and -not $isAdmin) {
    Write-Warning 'Il servizio Windows "LiveHelpDesk" e'' installato: rilanciare con -AllUsers come amministratore per rimuoverlo.'
}

# Chiudere l'app e il processo monitor avviati da questa cartella.
Get-Process -Name 'LiveHelpDesk', 'LiveHelpDesk.Service' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Write-Host "Chiudo $($_.Name) ($($_.Id))..."; $_ | Stop-Process -Force; $_.WaitForExit(10000) | Out-Null }

# Scorciatoia, avvio automatico e voce di disinstallazione.
$programs = [Environment]::GetFolderPath($(if ($AllUsers) { 'CommonPrograms' } else { 'Programs' }))
Remove-Item (Join-Path $programs 'LiveHelpDesk.lnk') -Force -ErrorAction SilentlyContinue

$runKey = if ($AllUsers) { 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' }
Remove-ItemProperty $runKey -Name LiveHelpDesk -ErrorAction SilentlyContinue

$uninstallKey = if ($AllUsers) { 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LiveHelpDesk' } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveHelpDesk' }
Remove-Item $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $InstallDir) {
    Write-Host "Rimuovo i file da $InstallDir ..."
    Remove-Item $InstallDir -Recurse -Force
}

if ($RemoveData) {
    foreach ($data in (Join-Path $env:ProgramData 'LiveHelpDesk'), (Join-Path $env:LOCALAPPDATA 'LiveHelpDesk')) {
        if (Test-Path $data) { Write-Host "Elimino i dati in $data ..."; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
else {
    Write-Host 'I dati salvati (configurazione, regole, canali, log) sono stati conservati. Usare -RemoveData per eliminarli.'
}

Write-Host 'LiveHelpDesk disinstallato.'
