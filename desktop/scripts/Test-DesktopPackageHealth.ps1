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
    "python-runtime\python.exe",
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
    "requirements-runtime.lock",
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
    "Test-DesktopBundledPythonSmoke.ps1",
    "Test-DesktopInstallSmoke.ps1",
    "Test-DesktopDiagnosticsRedactionSmoke.ps1",
    "release-info.txt",
    "INSTALL.txt"
)

$requiredDirectories = @(
    "python-wheelhouse"
)

$missing = @()
foreach ($relativePath in $requiredFiles) {
    if (!(Test-Path (Join-Path $packageFull $relativePath))) {
        $missing += $relativePath
    }
}

$missingDirectories = @()
foreach ($relativePath in $requiredDirectories) {
    $directoryPath = Join-Path $packageFull $relativePath
    if (!(Test-Path $directoryPath) -or !(Get-ChildItem -LiteralPath $directoryPath -File -Filter "*.whl" | Select-Object -First 1)) {
        $missingDirectories += $relativePath
    }
}

$pythonRuntimePath = Join-Path $packageFull "python-runtime\python.exe"
$pythonRuntimeProbePassed = $false
$pythonRuntimeProbeMessage = ""
if (Test-Path $pythonRuntimePath) {
    $previousDontWriteBytecode = $env:PYTHONDONTWRITEBYTECODE
    $env:PYTHONDONTWRITEBYTECODE = "1"
    $probeOutput = & $pythonRuntimePath -c "import sys, pandas, numpy, openpyxl, chardet, e_detection.cli; print(sys.executable)" 2>&1
    $env:PYTHONDONTWRITEBYTECODE = $previousDontWriteBytecode
    $pythonRuntimeProbePassed = $LASTEXITCODE -eq 0
    $pythonRuntimeProbeMessage = ($probeOutput -join [Environment]::NewLine)
    $global:LASTEXITCODE = 0
}

$pthFile = Get-ChildItem -LiteralPath (Join-Path $packageFull "python-runtime") -File -Filter "python*._pth" -ErrorAction SilentlyContinue | Select-Object -First 1
$pythonRuntimePathFileExists = $null -ne $pthFile

$nestedPublishPath = Join-Path $packageFull "publish"
$nestedPublishExists = Test-Path $nestedPublishPath
$smokeResultsPath = Join-Path $packageFull "smoke-results"
$smokeResultsExists = Test-Path $smokeResultsPath
$nestedArtifactsPath = Join-Path $packageFull "artifacts"
$nestedArtifactsExists = Test-Path $nestedArtifactsPath
$pythonCacheEntries = Get-ChildItem -LiteralPath $packageFull -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object {
        ($_.PSIsContainer -and $_.Name -eq "__pycache__") -or
        (!$_.PSIsContainer -and ($_.Name.EndsWith(".pyc", [System.StringComparison]::OrdinalIgnoreCase) -or $_.Name.EndsWith(".pyo", [System.StringComparison]::OrdinalIgnoreCase)))
    } |
    Select-Object -First 5 -ExpandProperty FullName

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
    RequiredDirectoryCount = $requiredDirectories.Count
    Missing = $missing
    MissingDirectories = $missingDirectories
    PythonRuntimeProbePassed = $pythonRuntimeProbePassed
    PythonRuntimePathFileExists = $pythonRuntimePathFileExists
    PythonRuntimeProbeMessage = $pythonRuntimeProbeMessage
    NestedPublishExists = $nestedPublishExists
    SmokeResultsExists = $smokeResultsExists
    NestedArtifactsExists = $nestedArtifactsExists
    PythonCacheEntries = @($pythonCacheEntries)
    Passed = ($missing.Count -eq 0 -and $missingDirectories.Count -eq 0 -and $pythonRuntimeProbePassed -and $pythonRuntimePathFileExists -and !$nestedPublishExists -and !$smokeResultsExists -and !$nestedArtifactsExists -and @($pythonCacheEntries).Count -eq 0)
    CheckedAt = (Get-Date).ToString("o")
}

if ($AsJson) {
    $result | ConvertTo-Json
}

if (!$result.Passed) {
    if ($missing.Count -gt 0) {
        Write-Error "Package health failed: missing $($missing -join ', ')"
    }

    if ($missingDirectories.Count -gt 0) {
        Write-Error "Package health failed: missing wheelhouse directory or wheels $($missingDirectories -join ', ')"
    }

    if (!$pythonRuntimePathFileExists) {
        Write-Error "Package health failed: bundled Python ._pth file was not found."
    }

    if (!$pythonRuntimeProbePassed) {
        Write-Error "Package health failed: bundled Python runtime probe failed. $pythonRuntimeProbeMessage"
    }

    if ($nestedPublishExists) {
        Write-Error "Package health failed: nested publish directory exists at $nestedPublishPath"
    }

    if ($smokeResultsExists) {
        Write-Error "Package health failed: smoke results directory exists at $smokeResultsPath"
    }

    if ($nestedArtifactsExists) {
        Write-Error "Package health failed: nested artifacts directory exists at $nestedArtifactsPath"
    }

    if (@($pythonCacheEntries).Count -gt 0) {
        Write-Error "Package health failed: Python bytecode/cache entries exist: $($pythonCacheEntries -join ', ')"
    }

    exit 1
}

if (!$AsJson) {
    Write-Host "Package health passed: $packageFull"
}
