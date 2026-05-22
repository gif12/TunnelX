# Pre-push sanity check for TunnelX native engines and embedded resources.
# Usage: .\scripts\verify-prerequisites.ps1 [-Configuration Release]

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$proj = Join-Path $root "AppTunnel\AppTunnel.csproj"
$native = Join-Path $root "AppTunnel\NativeLibs\x64"

Write-Host "== TunnelX prerequisite verification ==" -ForegroundColor Cyan

$requiredNative = @("sing-box.exe", "WinDivert.dll", "WinDivert64.sys")
$optionalNative = @("wintun.dll", "xray.exe")

foreach ($name in $requiredNative) {
    $path = Join-Path $native $name
    if (-not (Test-Path $path)) {
        Write-Error "Missing required native file: $path"
    }
    $size = (Get-Item $path).Length
    Write-Host "[OK] $name ($size bytes)" -ForegroundColor Green
}

foreach ($name in $optionalNative) {
    $path = Join-Path $native $name
    if (Test-Path $path) {
        $size = (Get-Item $path).Length
        Write-Host "[OK] $name ($size bytes)" -ForegroundColor Green
    }
    else {
        Write-Host "[WARN] $name not in NativeLibs (xhttp builds need xray.exe)" -ForegroundColor Yellow
    }
}

Write-Host "`nBuilding $Configuration..." -ForegroundColor Cyan
dotnet build $proj -c $Configuration --no-restore 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    dotnet build $proj -c $Configuration
    exit $LASTEXITCODE
}

$dll = Join-Path $root "AppTunnel\bin\$Configuration\net8.0-windows\TunnelX.dll"
if (-not (Test-Path $dll)) {
    Write-Error "Build output not found: $dll"
}

$asm = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $dll))
$resources = $asm.GetManifestResourceNames() | Sort-Object
$expectedEmbedded = @("sing-box.exe", "WinDivert.dll", "WinDivert64.sys")
foreach ($name in $expectedEmbedded) {
    if ($resources -contains $name) {
        Write-Host "[OK] embedded resource: $name" -ForegroundColor Green
    }
    else {
        Write-Warning "Embedded resource missing in assembly: $name (regular build may use side-by-side files)"
    }
}

if ($resources -contains "wintun.dll") {
    Write-Host "[OK] embedded resource: wintun.dll" -ForegroundColor Green
}
else {
    Write-Host "[WARN] embedded wintun.dll missing" -ForegroundColor Yellow
}

if ($resources -contains "xray.exe") {
    Write-Host "[OK] embedded resource: xray.exe" -ForegroundColor Green
}

Write-Host "`nPath constants (must match providers):" -ForegroundColor Cyan
Write-Host "  singbox work dir: %LOCALAPPDATA%\TunnelX\singbox"
Write-Host "  xray work dir:    %LOCALAPPDATA%\TunnelX\xray"
Write-Host "  app native dir:   %LOCALAPPDATA%\TunnelX"

Write-Host "`nAll automated checks passed." -ForegroundColor Green
