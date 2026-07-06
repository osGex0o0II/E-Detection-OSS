param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$DotNetPath = "",

    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")
$projectPath = Join-Path $repoRoot "desktop\EDetection.Desktop\EDetection.Desktop.csproj"
$solutionPath = Join-Path $repoRoot "desktop\EDetection.Desktop.slnx"
$artifactRoot = Join-Path $repoRoot "artifacts\desktop\$RuntimeIdentifier"
$publishDir = Join-Path $artifactRoot "publish"
$zipPath = Join-Path $artifactRoot "E-Detection.Desktop-$RuntimeIdentifier.zip"

if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $localDotNet = Join-Path $repoRoot "build\dotnet\dotnet.exe"
    if (Test-Path $localDotNet) {
        $DotNetPath = $localDotNet
    }
    else {
        $DotNetPath = "dotnet"
    }
}

$artifactRootFull = [System.IO.Path]::GetFullPath($artifactRoot)
$publishDirFull = [System.IO.Path]::GetFullPath($publishDir)
if (!$publishDirFull.StartsWith($artifactRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean publish directory outside artifact root: $publishDirFull"
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
if (Test-Path $publishDirFull) {
    Remove-Item -LiteralPath $publishDirFull -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDirFull | Out-Null

Write-Host "Using dotnet: $DotNetPath"
Write-Host "Restoring desktop solution..."
& $DotNetPath restore $solutionPath

Write-Host "Publishing $RuntimeIdentifier..."
& $DotNetPath publish $projectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    --no-restore `
    -o $publishDir

$exePath = Join-Path $publishDir "EDetection.Desktop.exe"
if (!(Test-Path $exePath)) {
    throw "Publish failed: EDetection.Desktop.exe was not found in $publishDir"
}

$iconPath = Join-Path $publishDir "Assets\Icons\app.ico"
if (!(Test-Path $iconPath)) {
    throw "Publish failed: Assets\Icons\app.ico was not copied to $publishDir"
}

$runningIconPath = Join-Path $publishDir "Assets\Icons\running.ico"
if (!(Test-Path $runningIconPath)) {
    throw "Publish failed: Assets\Icons\running.ico was not copied to $publishDir"
}

$deliveryScripts = @(
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
    "Test-DesktopInstallSmoke.ps1"
)

foreach ($scriptName in $deliveryScripts) {
    $scriptPath = Join-Path $scriptDir $scriptName
    if (!(Test-Path $scriptPath)) {
        throw "Publish failed: delivery script $scriptName was not found in $scriptDir"
    }

    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $publishDir $scriptName) -Force
}

$requiredWinUIResources = @(
    "App.xbf",
    "MainWindow.xbf",
    "EDetection.Desktop.pri",
    "Styles\Common.xbf",
    "Views\AppShellView.xbf",
    "Views\DetectionWorkbenchView.xbf",
    "Views\RunSetupView.xbf",
    "Views\SettingsView.xbf"
)

foreach ($resource in $requiredWinUIResources) {
    $resourcePath = Join-Path $publishDir $resource
    if (!(Test-Path $resourcePath)) {
        throw "Publish failed: WinUI resource '$resource' was not copied to $publishDir"
    }
}

$gitCommit = ""
try {
    $gitCommit = (& git -C $repoRoot rev-parse --short HEAD).Trim()
}
catch {
    $gitCommit = "unknown"
}

$infoPath = Join-Path $publishDir "release-info.txt"
@(
    "E-Detection Desktop"
    "RuntimeIdentifier=$RuntimeIdentifier"
    "Configuration=$Configuration"
    "GitCommit=$gitCommit"
    "PublishedAt=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
    "EntryPoint=EDetection.Desktop.exe"
    "InstallScript=Install-Desktop.ps1"
    "UninstallScript=Uninstall-Desktop.ps1"
    "PythonCore=Requires a Python environment that can import e_detection"
) | Set-Content -Path $infoPath -Encoding UTF8

$installTextPath = Join-Path $publishDir "INSTALL.txt"
@(
    "E-Detection Desktop"
    ""
    "Install for the current Windows user:"
    "  powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1"
    ""
    "Install without shortcuts:"
    "  powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1 -NoDesktopShortcut -NoStartMenuShortcut"
    ""
    "Uninstall:"
    "  powershell -ExecutionPolicy Bypass -File .\Uninstall-Desktop.ps1"
    ""
    "The desktop app does not bundle Python yet. Configure Python in the app to point at an environment where e_detection is importable."
    ""
    "Validate install/uninstall without keeping user artifacts:"
    "  powershell -ExecutionPolicy Bypass -File .\Test-DesktopInstallSmoke.ps1"
) | Set-Content -Path $installTextPath -Encoding UTF8

foreach ($scriptName in $deliveryScripts) {
    if (!(Test-Path (Join-Path $publishDir $scriptName))) {
        throw "Publish failed: $scriptName was not copied to $publishDir"
    }
}

if (!(Test-Path $installTextPath)) {
    throw "Publish failed: INSTALL.txt was not created in $publishDir"
}

& (Join-Path $publishDir "Test-DesktopPackageHealth.ps1") -PackagePath $publishDir

if (!$NoZip) {
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "Creating archive..."
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
}

Write-Host "Publish output: $publishDir"
if (!$NoZip) {
    Write-Host "Archive: $zipPath"
}
