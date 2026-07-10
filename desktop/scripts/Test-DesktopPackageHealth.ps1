[CmdletBinding()]
param(
    [string]$PackagePath = "",

    [switch]$AllowUntrackedFiles,

    [switch]$AsJson
)

$ErrorActionPreference = "Stop"

function Get-RelativePathCompat([string]$Root, [string]$Path) {
    $rootUri = New-Object System.Uri(([System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'))
    $pathUri = New-Object System.Uri([System.IO.Path]::GetFullPath($Path))
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$packageFull = [System.IO.Path]::GetFullPath((Resolve-Path $PackagePath).Path)
$requiredFiles = @(
    "EDetection.exe",
    "Assets\Icons\app.ico",
    "Assets\Icons\running.ico",
    "App.xbf",
    "MainWindow.xbf",
    "EDetection.pri",
    "Styles\Common.xbf",
    "Views\AppShellView.xbf",
    "Views\DetectionWorkbenchView.xbf",
    "Views\RunSetupView.xbf",
    "Views\SettingsView.xbf",
    "config.json",
    "Install-Desktop.ps1",
    "Uninstall-Desktop.ps1",
    "Test-DesktopPackageHealth.ps1",
    "Test-DesktopVisualSmoke.ps1",
    "Test-DesktopTrayMenuSmoke.ps1",
    "Test-DesktopSingleInstanceSmoke.ps1",
    "Test-DesktopSessionEndingSmoke.ps1",
    "Test-DesktopRunStateSmoke.ps1",
    "Test-DesktopRunCompletionSmoke.ps1",
    "Test-DesktopNativeRunReportSmoke.ps1",
    "Test-DesktopSettingsSmoke.ps1",
    "Test-DesktopStartupIntegrationSmoke.ps1",
    "Test-DesktopInstallSmoke.ps1",
    "Test-DesktopDiagnosticsRedactionSmoke.ps1",
    "Test-DesktopSignatureStatus.ps1",
    "release-info.txt",
    "install-files.txt",
    "INSTALL.txt"
)

$forbiddenLegacyEntries = @(
    "python-runtime",
    "python-wheelhouse",
    "core",
    "e_detection",
    "pyproject.toml",
    "requirements.txt",
    "requirements-runtime.lock",
    "Test-DesktopEnvironmentRepairSmoke.ps1",
    "Test-DesktopBundledPythonSmoke.ps1"
)

$missing = @($requiredFiles | Where-Object { !(Test-Path (Join-Path $packageFull $_)) })
$forbiddenEntries = @($forbiddenLegacyEntries | Where-Object { Test-Path (Join-Path $packageFull $_) })

$releaseInfoPath = Join-Path $packageFull "release-info.txt"
$releaseInfoEntries = @{}
if (Test-Path $releaseInfoPath) {
    foreach ($line in Get-Content -LiteralPath $releaseInfoPath) {
        $separatorIndex = $line.IndexOf("=")
        if ($separatorIndex -gt 0) {
            $releaseInfoEntries[$line.Substring(0, $separatorIndex)] = $line.Substring($separatorIndex + 1)
        }
    }
}

$releaseInfoMismatches = @()
$actualDetectionBackend = if ($releaseInfoEntries.ContainsKey("DetectionBackend")) { [string]$releaseInfoEntries["DetectionBackend"] } else { "" }
if ($actualDetectionBackend -ne "native") {
    $releaseInfoMismatches += "DetectionBackend=$actualDetectionBackend expected native"
}

$installerGeneratedManifestNames = @(
    "install-files.txt",
    "install-manifest.json",
    "unins000.exe",
    "unins000.dat",
    "unins000.msg"
)
$installFilesManifestPath = Join-Path $packageFull "install-files.txt"
$installFilesManifestMismatches = @()
if (Test-Path $installFilesManifestPath) {
    $expectedManifestEntries = Get-ChildItem -LiteralPath $packageFull -File -Recurse -Force |
        ForEach-Object { Get-RelativePathCompat $packageFull $_.FullName } |
        Where-Object { $_ -notin $installerGeneratedManifestNames } |
        Sort-Object
    $actualManifestEntries = Get-Content -LiteralPath $installFilesManifestPath | Sort-Object
    if ($AllowUntrackedFiles) {
        $installFilesManifestMismatches = @($actualManifestEntries | Where-Object {
            !(Test-Path -LiteralPath (Join-Path $packageFull $_))
        } | Select-Object -First 10 | ForEach-Object { "<= $_" })
    }
    else {
        $installFilesManifestMismatches = @(Compare-Object -ReferenceObject $expectedManifestEntries -DifferenceObject $actualManifestEntries |
            Select-Object -First 10 | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
    }
}
else {
    $installFilesManifestMismatches += "Missing install-files.txt"
}

$nestedPublishPath = Join-Path $packageFull "publish"
$smokeResultsPath = Join-Path $packageFull "smoke-results"
$nestedArtifactsPath = Join-Path $packageFull "artifacts"
$legacyCacheEntries = Get-ChildItem -LiteralPath $packageFull -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object {
        ($_.PSIsContainer -and $_.Name -eq "__pycache__") -or
        ($_.PSIsContainer -and $_.Name.EndsWith(".egg-info", [System.StringComparison]::OrdinalIgnoreCase)) -or
        (!$_.PSIsContainer -and ($_.Name.EndsWith(".pyc", [System.StringComparison]::OrdinalIgnoreCase) -or $_.Name.EndsWith(".pyo", [System.StringComparison]::OrdinalIgnoreCase)))
    } | Select-Object -First 5 -ExpandProperty FullName

$exePath = Join-Path $packageFull "EDetection.exe"
$exeVersion = if (Test-Path $exePath) { [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion } else { $null }
$result = [pscustomobject]@{
    PackagePath = $packageFull
    DetectionBackend = "native"
    EntryPoint = $exePath
    EntryPointVersion = $exeVersion
    RequiredFileCount = $requiredFiles.Count
    Missing = $missing
    ForbiddenEntries = $forbiddenEntries
    ReleaseInfoMismatches = $releaseInfoMismatches
    InstallFilesManifestMismatches = $installFilesManifestMismatches
    NestedPublishExists = (Test-Path $nestedPublishPath)
    SmokeResultsExists = (Test-Path $smokeResultsPath)
    NestedArtifactsExists = (Test-Path $nestedArtifactsPath)
    LegacyCacheEntries = @($legacyCacheEntries)
    Passed = ($missing.Count -eq 0 -and $forbiddenEntries.Count -eq 0 -and $releaseInfoMismatches.Count -eq 0 -and $installFilesManifestMismatches.Count -eq 0 -and !(Test-Path $nestedPublishPath) -and !(Test-Path $smokeResultsPath) -and !(Test-Path $nestedArtifactsPath) -and @($legacyCacheEntries).Count -eq 0)
    CheckedAt = (Get-Date).ToString("o")
}

if ($AsJson) { $result | ConvertTo-Json }

if (!$result.Passed) {
    if ($missing.Count -gt 0) { Write-Error "Package health failed: missing $($missing -join ', ')" }
    if ($forbiddenEntries.Count -gt 0) { Write-Error "Package health failed: native package contains removed legacy entries $($forbiddenEntries -join ', ')" }
    if ($releaseInfoMismatches.Count -gt 0) { Write-Error "Package health failed: release-info mismatch $($releaseInfoMismatches -join '; ')" }
    if ($installFilesManifestMismatches.Count -gt 0) { Write-Error "Package health failed: install-files manifest mismatch $($installFilesManifestMismatches -join '; ')" }
    if (Test-Path $nestedPublishPath) { Write-Error "Package health failed: nested publish directory exists at $nestedPublishPath" }
    if (Test-Path $smokeResultsPath) { Write-Error "Package health failed: smoke results directory exists at $smokeResultsPath" }
    if (Test-Path $nestedArtifactsPath) { Write-Error "Package health failed: nested artifacts directory exists at $nestedArtifactsPath" }
    if (@($legacyCacheEntries).Count -gt 0) { Write-Error "Package health failed: removed legacy bytecode/cache entries exist: $($legacyCacheEntries -join ', ')" }
    exit 1
}

if (!$AsJson) { Write-Host "Native package health passed: $packageFull" }
