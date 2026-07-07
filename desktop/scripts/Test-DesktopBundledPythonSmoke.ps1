[CmdletBinding()]
param(
    [string]$PackagePath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$packageFull = [System.IO.Path]::GetFullPath((Resolve-Path $PackagePath).Path)
$pythonPath = Join-Path $packageFull "python-runtime\python.exe"
$configPath = Join-Path $packageFull "config.json"

if (!(Test-Path $pythonPath)) {
    throw "Bundled Python smoke failed: python.exe was not found at $pythonPath"
}

if (!(Test-Path $configPath)) {
    throw "Bundled Python smoke failed: config.json was not found at $configPath"
}

function Remove-PythonCacheEntries([string]$RootPath) {
    Get-ChildItem -LiteralPath $RootPath -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.PSIsContainer -and $_.Name -eq "__pycache__" } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Get-ChildItem -LiteralPath $RootPath -Recurse -Force -Include "*.pyc", "*.pyo" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

$previousDontWriteBytecode = $env:PYTHONDONTWRITEBYTECODE
$env:PYTHONDONTWRITEBYTECODE = "1"

try {
    $probe = & $pythonPath -B -c "import sys, pandas, numpy, openpyxl, chardet, e_detection.cli; print(sys.executable)" 2>&1
}
finally {
    $env:PYTHONDONTWRITEBYTECODE = $previousDontWriteBytecode
}

if ($LASTEXITCODE -ne 0) {
    throw "Bundled Python smoke failed: import probe failed. $($probe -join ' ')"
}

$executableLine = ($probe | Select-Object -First 1)
if ($executableLine -notlike "*python-runtime*") {
    throw "Bundled Python smoke failed: sys.executable did not point at python-runtime. Output: $executableLine"
}

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) "E-Detection-Desktop-BundledPythonSmoke"
$inputDir = Join-Path $smokeRoot "input"
if (Test-Path $smokeRoot) {
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $inputDir | Out-Null
Set-Content -Path (Join-Path $inputDir "demo.csv") -Value "time,Uab`n0,380`n" -Encoding UTF8

try {
    $previousDontWriteBytecode = $env:PYTHONDONTWRITEBYTECODE
    $env:PYTHONDONTWRITEBYTECODE = "1"
    $events = & $pythonPath -B -m e_detection --json-events --no-report --config $configPath $inputDir 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Bundled Python smoke failed: detection command exited with $LASTEXITCODE. $($events -join ' ')"
    }

    $completed = $events | Where-Object { $_ -like '*"event": "run_completed"*' -or $_ -like '*"event":"run_completed"*' } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($completed)) {
        throw "Bundled Python smoke failed: run_completed event was not emitted. $($events -join ' ')"
    }
}
finally {
    $env:PYTHONDONTWRITEBYTECODE = $previousDontWriteBytecode
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-PythonCacheEntries $packageFull
}

Write-Host "Bundled Python smoke passed: $pythonPath"
