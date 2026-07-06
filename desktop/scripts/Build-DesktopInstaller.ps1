param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",

    [string]$SourcePath = "",

    [string]$OutputDirectory = "",

    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")
$projectPath = Join-Path $repoRoot "desktop\EDetection.Desktop\EDetection.Desktop.csproj"
$installerScript = Join-Path $repoRoot "desktop\installer\E-Detection.Desktop.iss"
$artifactRoot = Join-Path $repoRoot "artifacts\desktop\$RuntimeIdentifier"

if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $artifactRoot "publish"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactRoot "installer"
}

function Resolve-IsccPath {
    param([string]$RequestedPath)

    if (![string]::IsNullOrWhiteSpace($RequestedPath)) {
        return $RequestedPath
    }

    if (![string]::IsNullOrWhiteSpace($env:ISCC_PATH)) {
        return $env:ISCC_PATH
    }

    $command = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Inno Setup compiler ISCC.exe was not found. Install Inno Setup 6 or pass -IsccPath."
}

$sourceFull = [System.IO.Path]::GetFullPath((Resolve-Path $SourcePath).Path)
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
$artifactRootFull = [System.IO.Path]::GetFullPath($artifactRoot)

if (!$outputFull.StartsWith($artifactRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean installer output outside artifact root: $outputFull"
}

if (!(Test-Path (Join-Path $sourceFull "EDetection.Desktop.exe"))) {
    throw "Installer build failed: EDetection.Desktop.exe was not found in $sourceFull"
}

if (!(Test-Path $installerScript)) {
    throw "Installer build failed: Inno script was not found at $installerScript"
}

$healthScript = Join-Path $sourceFull "Test-DesktopPackageHealth.ps1"
if (!(Test-Path $healthScript)) {
    throw "Installer build failed: Test-DesktopPackageHealth.ps1 was not found in $sourceFull"
}

& $healthScript -PackagePath $sourceFull

New-Item -ItemType Directory -Force -Path $outputFull | Out-Null
Get-ChildItem -LiteralPath $outputFull -Force | Remove-Item -Recurse -Force

$isccFull = Resolve-IsccPath $IsccPath
if (!(Test-Path $isccFull)) {
    throw "Inno Setup compiler was not found at $isccFull"
}

[xml]$project = Get-Content -Path $projectPath
$appVersion = $project.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    $appVersion = "0.1.0"
}

Write-Host "Using Inno Setup compiler: $isccFull"
Write-Host "Building installer for $RuntimeIdentifier..."

& $isccFull `
    "/DSourceDir=$sourceFull" `
    "/DOutputDir=$outputFull" `
    "/DAppVersion=$appVersion" `
    "/DRuntimeIdentifier=$RuntimeIdentifier" `
    $installerScript

if ($LASTEXITCODE -ne 0) {
    throw "Installer build failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $outputFull "E-Detection.Desktop-Setup-$RuntimeIdentifier.exe"
if (!(Test-Path $installerPath)) {
    throw "Installer build failed: expected installer was not found at $installerPath"
}

Write-Host "Installer output: $installerPath"
