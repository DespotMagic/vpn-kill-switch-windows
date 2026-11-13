@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0vpn-killswitch.ps1" status
echo Press any key to exit...
pause >nul