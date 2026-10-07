@echo off
setlocal
cd /d "%~dp0"

if not exist "dist\BuckshotOverlay.exe" (
    echo Building Buckshot Overlay...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
)

echo Starting Buckshot Roulette Tactical Overlay...
start "" "%~dp0dist\BuckshotOverlay.exe"
exit /b 0
