[CmdletBinding()]
param(
    [string]$PackagePath = "",

    [switch]$AsJson
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$packageFull = [System.IO.Path]::GetFullPath((Resolve-Path $PackagePath).Path)

$requiredFiles = @(
    "EDetection.Desktop.exe",
    "Assets\Icons\app.ico",
    "Assets\Icons\running.ico",
    "App.xbf",
    "MainWindow.xbf",
    "EDetection.Desktop.pri",
    "Styles\Common.xbf",
    "Views\AppShellView.xbf",
    "Views\DetectionWorkbenchView.xbf",
    "Views\RunSetupView.xbf",
    "Views\SettingsView.xbf",
    "pyproject.toml",
    "config.json",
    "requirements.txt",
    "core\rules.py",
    "core\rule_base.py",
    "e_detection\__main__.py",
    "e_detection\cli.py",
    "e_detection\batch.py",
    "e_detection\pipeline.py",
    "e_detection\settings.py",
    "Install-Desktop.ps1",
    "Uninstall-Desktop.ps1",
    "Test-DesktopPackageHealth.ps1",
    "Test-DesktopVisualSmoke.ps1",
    "Test-DesktopTrayMenuSmoke.ps1",
    "Test-DesktopSingleInstanceSmoke.ps1",
    "Test-DesktopSessionEndingSmoke.ps1",
    "Test-DesktopRunStateSmoke.ps1",
    "Test-DesktopRunCompletionSmoke.ps1",
    "Test-DesktopSettingsSmoke.ps1",
    "Test-DesktopStartupIntegrationSmoke.ps1",
    "Test-DesktopEnvironmentRepairSmoke.ps1",
    "Test-DesktopInstallSmoke.ps1",
    "release-info.txt",
    "INSTALL.txt"
)

$missing = @()
foreach ($relativePath in $requiredFiles) {
    if (!(Test-Path (Join-Path $packageFull $relativePath))) {
        $missing += $relativePath
    }
}

$nestedPublishPath = Join-Path $packageFull "publish"
$nestedPublishExists = Test-Path $nestedPublishPath

$exePath = Join-Path $packageFull "EDetection.Desktop.exe"
$exeVersion = $null
if (Test-Path $exePath) {
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
    $exeVersion = $versionInfo.FileVersion
}

$result = [pscustomobject]@{
    PackagePath = $packageFull
    EntryPoint = $exePath
    EntryPointVersion = $exeVersion
    RequiredFileCount = $requiredFiles.Count
    Missing = $missing
    NestedPublishExists = $nestedPublishExists
    Passed = ($missing.Count -eq 0 -and !$nestedPublishExists)
    CheckedAt = (Get-Date).ToString("o")
}

if ($AsJson) {
    $result | ConvertTo-Json
}

if (!$result.Passed) {
    if ($missing.Count -gt 0) {
        Write-Error "Package health failed: missing $($missing -join ', ')"
    }

    if ($nestedPublishExists) {
        Write-Error "Package health failed: nested publish directory exists at $nestedPublishPath"
    }

    exit 1
}

if (!$AsJson) {
    Write-Host "Package health passed: $packageFull"
}
