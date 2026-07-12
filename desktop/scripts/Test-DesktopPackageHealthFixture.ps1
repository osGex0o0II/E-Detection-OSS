[CmdletBinding()]
param(
    [string]$PackageHealthScriptPath = "",

    [string]$ScratchRoot = ""
)

$ErrorActionPreference = "Stop"

function Get-RelativePathCompat([string]$Root, [string]$Path) {
    $rootUri = New-Object System.Uri(([System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'))
    $pathUri = New-Object System.Uri([System.IO.Path]::GetFullPath($Path))
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")

if ([string]::IsNullOrWhiteSpace($PackageHealthScriptPath)) {
    $PackageHealthScriptPath = Join-Path $scriptDir "Test-DesktopPackageHealth.ps1"
}

if ([string]::IsNullOrWhiteSpace($ScratchRoot)) {
    $ScratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) "E-Detection-PackageHealthFixture-$([Guid]::NewGuid().ToString('N'))"
}

$packageHealthFull = [System.IO.Path]::GetFullPath((Resolve-Path $PackageHealthScriptPath).Path)
$scratchFull = [System.IO.Path]::GetFullPath($ScratchRoot)

function New-FixtureFile([string]$Root, [string]$RelativePath, [string]$Content = "fixture") {
    $path = Join-Path $Root $RelativePath
    $directory = Split-Path -Parent $path
    if (![string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    Set-Content -Path $path -Value $Content -Encoding UTF8
}

function Write-InstallFilesManifest([string]$Root) {
    $manifestPath = Join-Path $Root "install-files.txt"
    Get-ChildItem -LiteralPath $Root -File -Recurse -Force |
        ForEach-Object { Get-RelativePathCompat $Root $_.FullName } |
        Where-Object { $_ -ne "install-files.txt" -and $_ -ne "install-manifest.json" } |
        Sort-Object |
        Set-Content -Path $manifestPath -Encoding UTF8
}

function New-NativePackageFixture([string]$Root) {
    $commonRequiredFiles = @(
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
        "DesktopPathSafety.ps1",
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
        "Test-DesktopScriptSafetySmoke.ps1",
        "INSTALL.txt"
    )

    foreach ($relativePath in $commonRequiredFiles) {
        New-FixtureFile $Root $relativePath
    }

    New-FixtureFile `
        $Root `
        "release-info.txt" `
        "DetectionBackend=native`n"
    Write-InstallFilesManifest $Root
}

function Copy-FixturePackage([string]$Source, [string]$Destination) {
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

function Invoke-PackageHealth([string]$PackagePath) {
    $powerShellExe = (Get-Process -Id $PID).Path
    $output = & $powerShellExe `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $packageHealthFull `
        -PackagePath $PackagePath `
        -AsJson 2>&1
    $exitCode = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = ($output -join [Environment]::NewLine)
    }
}

function Convert-HealthJson([string]$Text) {
    $start = $Text.IndexOf("{")
    $end = $Text.LastIndexOf("}")
    if ($start -lt 0 -or $end -lt $start) {
        throw "Package health fixture failed: JSON result was not found. Output: $Text"
    }

    return $Text.Substring($start, $end - $start + 1) | ConvertFrom-Json
}

function Assert-HealthPass([string]$PackagePath, [string]$Scenario) {
    $run = Invoke-PackageHealth $PackagePath
    if ($run.ExitCode -ne 0) {
        throw "Package health fixture failed: expected pass for $Scenario, exit=$($run.ExitCode). Output: $($run.Text)"
    }

    $json = Convert-HealthJson $run.Text
    if (!$json.Passed) {
        throw "Package health fixture failed: JSON result did not pass for $Scenario. Output: $($run.Text)"
    }
}

function Assert-HealthFailContains([string]$PackagePath, [string]$ExpectedText, [string]$Scenario) {
    $run = Invoke-PackageHealth $PackagePath
    if ($run.ExitCode -eq 0) {
        throw "Package health fixture failed: expected failure for $Scenario."
    }

    if ($run.Text.IndexOf($ExpectedText, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Package health fixture failed: expected '$ExpectedText' for $Scenario. Output: $($run.Text)"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $scratchFull | Out-Null

    $validPackage = Join-Path $scratchFull "native-valid"
    New-Item -ItemType Directory -Force -Path $validPackage | Out-Null
    New-NativePackageFixture $validPackage
    Assert-HealthPass $validPackage "valid native package"

    $contaminatedPackage = Join-Path $scratchFull "native-legacy-contaminated"
    Copy-FixturePackage $validPackage $contaminatedPackage
    New-FixtureFile $contaminatedPackage "python-runtime\python.exe"
    Write-InstallFilesManifest $contaminatedPackage
    Assert-HealthFailContains `
        $contaminatedPackage `
        "native package contains removed legacy entries" `
        "native package with removed legacy contamination"

    $releaseInfoMismatchPackage = Join-Path $scratchFull "native-release-info-mismatch"
    Copy-FixturePackage $validPackage $releaseInfoMismatchPackage
    New-FixtureFile `
        $releaseInfoMismatchPackage `
        "release-info.txt" `
        "DetectionBackend=legacy`n"
    Assert-HealthFailContains `
        $releaseInfoMismatchPackage `
        "release-info mismatch" `
        "native package with non-native backend metadata"

    $manifestMismatchPackage = Join-Path $scratchFull "native-manifest-mismatch"
    Copy-FixturePackage $validPackage $manifestMismatchPackage
    Add-Content -Path (Join-Path $manifestMismatchPackage "install-files.txt") -Value "stale-extra-file.txt"
    Assert-HealthFailContains `
        $manifestMismatchPackage `
        "install-files manifest mismatch" `
        "native package with stale install-files manifest"
}
finally {
    Remove-Item -LiteralPath $scratchFull -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Package health fixture passed: $packageHealthFull"
