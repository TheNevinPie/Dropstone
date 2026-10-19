@echo off
rem Run a sync now (tops up all existing LeviLauncher version folders).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Sync.ps1"
pause
