[CmdletBinding()]
param(
    [string]$PackagePath = "",

    [string]$SourceRoot = "",

    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = if (Test-Path (Join-Path $scriptDir "EDetection.exe")) {
        $scriptDir
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\win-x64\publish"
    }
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $ancestorRoot = Resolve-Path (Join-Path $scriptDir "..\..\..") -ErrorAction SilentlyContinue
    $sourceCandidates = @(
        (Join-Path (Get-Location).Path "desktop\EDetection.Desktop"),
        (Join-Path $repoRoot "desktop\EDetection.Desktop"),
        $(if ($ancestorRoot) { Join-Path $ancestorRoot.Path "desktop\EDetection.Desktop" })
    )
    $SourceRoot = ($sourceCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1)
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    throw "Tray-menu smoke failed: could not locate desktop source root. Pass -SourceRoot explicitly."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = if (Test-Path (Join-Path $scriptDir "EDetection.exe")) {
        Join-Path $scriptDir "smoke-results\tray-menu"
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\tray-menu-smoke"
    }
}

$packageFull = [System.IO.Path]::GetFullPath((Resolve-Path $PackagePath).Path)
$sourceFull = [System.IO.Path]::GetFullPath((Resolve-Path $SourceRoot).Path)
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputFull | Out-Null

$trayServicePath = Join-Path $sourceFull "Services\TrayIconService.cs"
$mainWindowPath = Join-Path $sourceFull "MainWindow.xaml.cs"
if (!(Test-Path $trayServicePath) -or !(Test-Path $mainWindowPath)) {
    throw "Tray-menu smoke failed: source files were not found under $sourceFull"
}

$traySource = Get-Content -LiteralPath $trayServicePath -Raw
$mainWindowSource = Get-Content -LiteralPath $mainWindowPath -Raw
$requiredTrayTokens = @(
    "开始检测",
    "取消检测",
    "打开最新报告",
    "打开报告目录",
    "ResolveCurrentIconHandle",
    "StartRequested",
    "CancelRequested",
    "OpenReportRequested",
    "OpenReportFolderRequested"
)

$requiredMainWindowTokens = @(
    "ViewModel.StartCommand",
    "ViewModel.CancelCommand",
    "ViewModel.OpenCurrentReportCommand",
    "ViewModel.OpenCurrentReportFolderCommand",
    "running.ico",
    "UpdateWindowIcon",
    "UpdateTrayCommands"
)

$missing = @()
foreach ($token in $requiredTrayTokens) {
    if ($traySource.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        $missing += "TrayIconService:$token"
    }
}

foreach ($token in $requiredMainWindowTokens) {
    if ($mainWindowSource.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
        $missing += "MainWindow:$token"
    }
}

$entryPoint = Join-Path $packageFull "EDetection.exe"
if (!(Test-Path $entryPoint)) {
    $missing += "Package:EDetection.exe"
}

$runningIcon = Join-Path $packageFull "Assets\Icons\running.ico"
if (!(Test-Path $runningIcon)) {
    $missing += "Package:Assets\\Icons\\running.ico"
}

if ($missing.Count -gt 0) {
    throw "Tray-menu smoke failed: missing $($missing -join ', ')"
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$resultPath = Join-Path $outputFull "tray-menu-smoke-$timestamp.json"
[pscustomobject]@{
    PackagePath = $packageFull
    SourceRoot = $sourceFull
    Commands = @("开始检测", "取消检测", "打开最新报告", "打开报告目录")
    RunningIcon = $runningIcon
    Passed = $true
    CapturedAt = (Get-Date).ToString("o")
} | ConvertTo-Json | Set-Content -Path $resultPath -Encoding UTF8

Write-Host "Tray-menu smoke passed: $resultPath"
