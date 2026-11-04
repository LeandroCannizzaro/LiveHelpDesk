@echo off
rem Avvia uninstall.ps1 passando gli stessi parametri (es. uninstall.cmd -AllUsers -RemoveData).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
