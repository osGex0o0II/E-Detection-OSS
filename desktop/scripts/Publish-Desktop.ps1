param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$DotNetPath = "",

    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

function Get-RelativePathCompat([string]$Root, [string]$Path) {
    $rootUri = New-Object System.Uri(([System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'))
    $pathUri = New-Object System.Uri([System.IO.Path]::GetFullPath($Path))
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")
$projectPath = Join-Path $repoRoot "desktop\EDetection.Desktop\EDetection.Desktop.csproj"
$solutionPath = Join-Path $repoRoot "desktop\EDetection.Desktop.slnx"
$artifactRoot = Join-Path $repoRoot "artifacts\desktop\$RuntimeIdentifier"
$publishDir = Join-Path $artifactRoot "publish"
$zipPath = Join-Path $artifactRoot "EDetection-$RuntimeIdentifier.zip"
$installerName = "EDetection-Setup-$RuntimeIdentifier.exe"
$installFilesManifestName = "install-files.txt"

function Get-RelativePackageFiles([string]$Path) {
    $rootFull = [System.IO.Path]::GetFullPath($Path)
    Get-ChildItem -LiteralPath $rootFull -File -Recurse -Force |
        ForEach-Object {
            Get-RelativePathCompat $rootFull $_.FullName
        } |
        Where-Object { $_ -ne $installFilesManifestName } |
        Sort-Object
}

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

Write-Host "Publishing native .NET desktop app for $RuntimeIdentifier..."
& $DotNetPath publish $projectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    --no-restore `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed: dotnet publish exited with $LASTEXITCODE."
}

$exePath = Join-Path $publishDir "EDetection.exe"
if (!(Test-Path $exePath)) {
    throw "Publish failed: EDetection.exe was not found in $publishDir"
}

$iconPath = Join-Path $publishDir "Assets\Icons\app.ico"
if (!(Test-Path $iconPath)) {
    throw "Publish failed: Assets\Icons\app.ico was not copied to $publishDir"
}

$runningIconPath = Join-Path $publishDir "Assets\Icons\running.ico"
if (!(Test-Path $runningIconPath)) {
    throw "Publish failed: Assets\Icons\running.ico was not copied to $publishDir"
}

$commonDeliveryScripts = @(
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
    "Test-DesktopSignatureStatus.ps1"
)

$deliveryScripts = $commonDeliveryScripts

foreach ($scriptName in $deliveryScripts) {
    $scriptPath = Join-Path $scriptDir $scriptName
    if (!(Test-Path $scriptPath)) {
        throw "Publish failed: delivery script $scriptName was not found in $scriptDir"
    }

    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $publishDir $scriptName) -Force
}

$configSourcePath = Join-Path $repoRoot "config.json"
if (!(Test-Path $configSourcePath)) {
    throw "Publish failed: config.json was not found in $repoRoot"
}
Copy-Item -LiteralPath $configSourcePath -Destination (Join-Path $publishDir "config.json") -Force

$requiredWinUIResources = @(
    "App.xbf",
    "MainWindow.xbf",
    "EDetection.pri",
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

$projectXml = [xml](Get-Content -Path $projectPath)
$appVersion = ($projectXml.Project.PropertyGroup.Version | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    $appVersion = "unknown"
}

$gitCommit = ""
$gitCommitFull = ""
$gitDirty = "unknown"
try {
    $gitCommit = (& git -C $repoRoot rev-parse --short HEAD).Trim()
    $gitCommitFull = (& git -C $repoRoot rev-parse HEAD).Trim()
    $dirtyOutput = (& git -C $repoRoot status --porcelain)
    $gitDirty = if ([string]::IsNullOrWhiteSpace(($dirtyOutput -join ""))) { "false" } else { "true" }
}
catch {
    $gitCommit = "unknown"
    $gitCommitFull = "unknown"
    $gitDirty = "unknown"
}

$infoPath = Join-Path $publishDir "release-info.txt"
@(
    "EDetection"
    "Version=$appVersion"
    "RuntimeIdentifier=$RuntimeIdentifier"
    "Configuration=$Configuration"
    "DetectionBackend=native"
    "GitCommit=$gitCommit"
    "GitCommitFull=$gitCommitFull"
    "GitDirty=$gitDirty"
    "PublishedAt=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
    "EntryPoint=EDetection.exe"
    "RecommendedInstaller=$installerName"
    "InstallScript=Install-Desktop.ps1"
    "UninstallScript=Uninstall-Desktop.ps1"
    "PackageContents=Native C# + .NET runtime only"
    "CodeSigning=Development packages may be unsigned; official GitHub Releases require Authenticode signing"
) | Set-Content -Path $infoPath -Encoding UTF8

$installTextPath = Join-Path $publishDir "INSTALL.txt"
@(
    "EDetection"
    ""
    "Recommended for most Windows users:"
    "  Download and run $installerName from GitHub Releases."
    "  The setup wizard lets you choose the install location and creates normal Windows shortcuts."
    "  To update, run the newer setup wizard and follow the on-screen prompts."
    "  To uninstall, use Windows Settings > Installed apps, or run unins000.exe from the install folder."
    ""
    "Advanced portable install for the current Windows user:"
    "  powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1"
    ""
    "Advanced install without shortcuts:"
    "  powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1 -NoDesktopShortcut -NoStartMenuShortcut"
    ""
    "Advanced install to a custom location:"
    "  powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1 -InstallDirectory ""D:\Apps\EDetection"""
    ""
    "Advanced portable uninstall:"
    "  powershell -ExecutionPolicy Bypass -File .\Uninstall-Desktop.ps1"
    "  If this command is run inside a setup-wizard install, it delegates to the Windows setup uninstaller."
    ""
    "This package runs the native C# + .NET detection backend."
    "Official GitHub Release installers are Authenticode signed. If Windows SmartScreen still warns on first install, verify the download came from the official GitHub Release and compare the attached SHA-256 checksum."
    "Checksum command:"
    "  Get-FileHash .\$installerName -Algorithm SHA256"
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

$installFilesManifestPath = Join-Path $publishDir $installFilesManifestName
Get-RelativePackageFiles $publishDir | Set-Content -Path $installFilesManifestPath -Encoding UTF8

$smokeResultsPath = Join-Path $publishDir "smoke-results"
if (Test-Path $smokeResultsPath) {
    Remove-Item -LiteralPath $smokeResultsPath -Recurse -Force
}

& (Join-Path $publishDir "Test-DesktopPackageHealth.ps1") -PackagePath $publishDir

$smokeResultsPath = Join-Path $publishDir "smoke-results"
if (Test-Path $smokeResultsPath) {
    Remove-Item -LiteralPath $smokeResultsPath -Recurse -Force
}

if (!$NoZip) {
    $legacyZipPaths = Get-ChildItem -LiteralPath $artifactRoot -File -Filter "E-Detection.Desktop-$RuntimeIdentifier*.zip"
    foreach ($legacyZipPath in $legacyZipPaths) {
        Remove-Item -LiteralPath $legacyZipPath.FullName -Force
    }

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
