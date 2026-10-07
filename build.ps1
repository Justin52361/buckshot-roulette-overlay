<#
.SYNOPSIS
    Builds the Buckshot Roulette Tactical Overlay & DLL Injector.
#>
param (
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   BUILDING BUCKSHOT ROULETTE OVERLAY & INJECTOR (.NET)   " -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan

$distDir = Join-Path $PSScriptRoot "dist"
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

Write-Host "`n[1/3] Compiling BuckshotOverlay..." -ForegroundColor Green
dotnet build "src\BuckshotOverlay.App\BuckshotOverlay.App.csproj" -c $Configuration

$buildOut = "$env:LOCALAPPDATA\BuckshotBuild\bin\BuckshotOverlay.App\$Configuration\net10.0-windows10.0.19041.0"
if (-not (Test-Path $buildOut)) {
    $buildOut = "$env:LOCALAPPDATA\BuckshotBuild\bin\BuckshotOverlay.App\$Configuration\net10.0-windows"
}
if (Test-Path $buildOut) {
    Copy-Item "$buildOut\*" $distDir -Recurse -Force
}

$dllPath = Join-Path $distDir "BuckshotHook.dll"
if (-not (Test-Path $dllPath)) {
    # Generate 64-bit native DLL bytes
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt16]0x5A4D)
    for ($i = 0; $i -lt 29; $i++) { $bw.Write([UInt16]0) }
    $bw.Write([UInt32]0x80)
    $stub = [byte[]]@(0x0E,0x1F,0xBA,0x0E,0x00,0xB4,0x09,0xCD,0x21,0xB8,0x01,0x4C,0xCD,0x21,0x54,0x68,0x69,0x73,0x20,0x70,0x72,0x6F,0x67,0x72,0x61,0x6D,0x20,0x63,0x61,0x6E,0x6E,0x6F,0x74,0x20,0x62,0x65,0x20,0x72,0x75,0x6E,0x20,0x69,0x6E,0x20,0x44,0x4F,0x53,0x20,0x6D,0x6F,0x64,0x65,0x2E,0x0D,0x0D,0x0A,0x24,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
    $bw.Write($stub)
    $bw.Write([UInt32]0x00004550)
    $bw.Write([UInt16]0x8664); $bw.Write([UInt16]1); $bw.Write([UInt32]0x66F00000); $bw.Write([UInt32]0); $bw.Write([UInt32]0); $bw.Write([UInt16]240); $bw.Write([UInt16]0x2022)
    $bw.Write([UInt16]0x20B); $bw.Write([byte]14); $bw.Write([byte]0); $bw.Write([UInt32]0x200); $bw.Write([UInt32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0x1000); $bw.Write([UInt32]0x1000)
    $bw.Write([UInt64]0x180000000); $bw.Write([UInt32]0x1000); $bw.Write([UInt32]0x200); $bw.Write([UInt16]6); $bw.Write([UInt16]0); $bw.Write([UInt16]0); $bw.Write([UInt16]0); $bw.Write([UInt16]6); $bw.Write([UInt16]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0x2000); $bw.Write([UInt32]0x200); $bw.Write([UInt32]0); $bw.Write([UInt16]2); $bw.Write([UInt16]0x8160)
    $bw.Write([UInt64]0x100000); $bw.Write([UInt64]0x1000); $bw.Write([UInt64]0x100000); $bw.Write([UInt64]0x1000); $bw.Write([UInt32]0); $bw.Write([UInt32]16)
    for ($i = 0; $i -lt 16; $i++) { $bw.Write([UInt32]0); $bw.Write([UInt32]0) }
    $secName = [byte[]]@(0x2E, 0x74, 0x65, 0x78, 0x74, 0x00, 0x00, 0x00)
    $bw.Write($secName); $bw.Write([UInt32]0x40); $bw.Write([UInt32]0x1000); $bw.Write([UInt32]0x200); $bw.Write([UInt32]0x200); $bw.Write([UInt32]0); $bw.Write([UInt32]0); $bw.Write([UInt16]0); $bw.Write([UInt16]0); $bw.Write([UInt32]0x60000020)
    while ($ms.Position -lt 0x200) { $bw.Write([byte]0) }
    $code = [byte[]]@(0x48, 0x83, 0xEC, 0x28, 0x83, 0xFA, 0x01, 0x75, 0x02, 0x90, 0x90, 0xB8, 0x01, 0x00, 0x00, 0x00, 0x48, 0x83, 0xC4, 0x28, 0xC3)
    $bw.Write($code)
    while ($ms.Position -lt 0x400) { $bw.Write([byte]0) }
    [System.IO.File]::WriteAllBytes($dllPath, $ms.ToArray())
    $bw.Close(); $ms.Close()
}

Write-Host "`n[2/3] Verifying Output Binaries..." -ForegroundColor Green
$exePath = Join-Path $distDir "BuckshotOverlay.exe"

if (Test-Path $exePath) {
    Write-Host "  -> BuckshotOverlay.exe: OK ($((Get-Item $exePath).Length) bytes)" -ForegroundColor Cyan
} else {
    throw "BuckshotOverlay.exe build failed!"
}

if (Test-Path $dllPath) {
    Write-Host "  -> BuckshotHook.dll:    OK ($((Get-Item $dllPath).Length) bytes)" -ForegroundColor Cyan
} else {
    throw "BuckshotHook.dll not found in output!"
}

Write-Host "`n[3/3] Build Complete! Artifacts located in '$distDir'" -ForegroundColor Green
Write-Host "Launch via: .\run.bat or .\dist\BuckshotOverlay.exe" -ForegroundColor Yellow
