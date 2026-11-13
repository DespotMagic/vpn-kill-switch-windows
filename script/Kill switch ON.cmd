@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0vpn-killswitch.ps1" on
echo Press any key to exit...
pause >nul