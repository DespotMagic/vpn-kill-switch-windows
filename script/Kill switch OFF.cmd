@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0vpn-killswitch.ps1" off
echo Press any key to exit...
pause >nul