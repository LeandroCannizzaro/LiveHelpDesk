@echo off
rem Avvia install.ps1 passando gli stessi parametri (es. install.cmd -AllUsers -InstallService).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
