param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$DotNetPath = "",

    [string]$PythonPath = "",

    [string]$BundledPythonVersion = "3.13.14",

    [switch]$SkipPythonWheelhouse,

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
$downloadDir = Join-Path $artifactRoot "downloads"
$runtimeRequirementsPath = Join-Path $repoRoot "desktop\requirements-runtime.lock"

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
    "Test-DesktopEnvironmentRepairSmoke.ps1",
    "Test-DesktopBundledPythonSmoke.ps1",
    "Test-DesktopInstallSmoke.ps1"
)

foreach ($scriptName in $deliveryScripts) {
    $scriptPath = Join-Path $scriptDir $scriptName
    if (!(Test-Path $scriptPath)) {
        throw "Publish failed: delivery script $scriptName was not found in $scriptDir"
    }

    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $publishDir $scriptName) -Force
}

$pythonCoreItems = @(
    @{ Source = "core"; Destination = "core" },
    @{ Source = "e_detection"; Destination = "e_detection" },
    @{ Source = "pyproject.toml"; Destination = "pyproject.toml" },
    @{ Source = "config.json"; Destination = "config.json" },
    @{ Source = "requirements.txt"; Destination = "requirements.txt" },
    @{ Source = "desktop\requirements-runtime.lock"; Destination = "requirements-runtime.lock" }
)

foreach ($item in $pythonCoreItems) {
    $sourcePath = Join-Path $repoRoot $item.Source
    if (!(Test-Path $sourcePath)) {
        throw "Publish failed: Python core item $($item.Source) was not found in $repoRoot"
    }

    $destinationPath = Join-Path $publishDir $item.Destination
    if (Test-Path $destinationPath) {
        Remove-Item -LiteralPath $destinationPath -Recurse -Force
    }

    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Recurse -Force
}

$pythonVersionParts = $BundledPythonVersion.Split(".")
if ($pythonVersionParts.Count -lt 2) {
    throw "Publish failed: BundledPythonVersion must look like 3.13.14."
}

$pythonMajorMinor = "$($pythonVersionParts[0]).$($pythonVersionParts[1])"
$pythonPthToken = "$($pythonVersionParts[0])$($pythonVersionParts[1])"
$pythonArchitecture = if ($RuntimeIdentifier -eq "win-arm64") { "arm64" } else { "amd64" }
$pipPlatform = if ($RuntimeIdentifier -eq "win-arm64") { "win_arm64" } else { "win_amd64" }
$pipAbi = "cp$pythonPthToken"
$runtimeZipName = "python-$BundledPythonVersion-embed-$pythonArchitecture.zip"
$runtimeZipPath = Join-Path $downloadDir $runtimeZipName
$runtimeUrl = "https://www.python.org/ftp/python/$BundledPythonVersion/$runtimeZipName"
$runtimeDir = Join-Path $publishDir "python-runtime"
$sitePackagesDir = Join-Path $runtimeDir "Lib\site-packages"
$expectedRuntimeHashes = @{
    "3.13.14|amd64" = "90b4e5b9898b72d744650524bff92377c367f44bd5fbd09e3148656c080ad907"
    "3.13.14|arm64" = "8b5bfc935a24b55c17410aa0b21016ebeee225c96addf008d1d3cd83ff52eb43"
}
$runtimeHashKey = "$BundledPythonVersion|$pythonArchitecture"
$expectedRuntimeHash = $expectedRuntimeHashes[$runtimeHashKey]
if ([string]::IsNullOrWhiteSpace($expectedRuntimeHash)) {
    throw "Publish failed: no SHA-256 pin is configured for bundled Python $runtimeHashKey."
}

New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
if (!(Test-Path $runtimeZipPath)) {
    Write-Host "Downloading bundled Python runtime: $runtimeUrl"
    Invoke-WebRequest -Uri $runtimeUrl -OutFile $runtimeZipPath
}

$actualRuntimeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeZipPath).Hash.ToLowerInvariant()
if ($actualRuntimeHash -ne $expectedRuntimeHash) {
    Remove-Item -LiteralPath $runtimeZipPath -Force -ErrorAction SilentlyContinue
    throw "Publish failed: bundled Python runtime SHA-256 mismatch for $runtimeZipName. Expected $expectedRuntimeHash but got $actualRuntimeHash."
}

if (Test-Path $runtimeDir) {
    Remove-Item -LiteralPath $runtimeDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
Expand-Archive -LiteralPath $runtimeZipPath -DestinationPath $runtimeDir -Force
New-Item -ItemType Directory -Force -Path $sitePackagesDir | Out-Null

$pthPath = Join-Path $runtimeDir "python$pythonPthToken._pth"
if (!(Test-Path $pthPath)) {
    throw "Publish failed: bundled Python ._pth file was not found at $pthPath"
}

@(
    "python$pythonPthToken.zip"
    "."
    "Lib\site-packages"
    ".."
    "import site"
) | Set-Content -Path $pthPath -Encoding ASCII

if (!$SkipPythonWheelhouse) {
    $wheelhouseDir = Join-Path $publishDir "python-wheelhouse"
    if (Test-Path $wheelhouseDir) {
        Remove-Item -LiteralPath $wheelhouseDir -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $wheelhouseDir | Out-Null
    if ([string]::IsNullOrWhiteSpace($PythonPath)) {
        $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
        if ($null -eq $pythonCommand) {
            throw "Publish failed: python was not found. Install Python or pass -SkipPythonWheelhouse."
        }

        $PythonPath = $pythonCommand.Source
    }

    Write-Host "Downloading Python wheelhouse..."
    & $PythonPath -m pip download `
        --platform $pipPlatform `
        --python-version $pythonMajorMinor `
        --implementation cp `
        --abi $pipAbi `
        --only-binary=:all: `
        --dest $wheelhouseDir `
        -r $runtimeRequirementsPath

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed: Python wheelhouse download failed with exit code $LASTEXITCODE."
    }

    if (-not (Get-ChildItem -LiteralPath $wheelhouseDir -File -Filter "*.whl" | Select-Object -First 1)) {
        throw "Publish failed: Python wheelhouse did not contain any .whl files."
    }

    Write-Host "Installing wheels into bundled Python runtime..."
    foreach ($wheel in Get-ChildItem -LiteralPath $wheelhouseDir -File -Filter "*.whl") {
        Expand-Archive -LiteralPath $wheel.FullName -DestinationPath $sitePackagesDir -Force
    }
}
else {
    throw "Publish failed: bundled Python runtime requires python-wheelhouse. Remove -SkipPythonWheelhouse for release packages."
}

$bundledPythonExe = Join-Path $runtimeDir "python.exe"
if (!(Test-Path $bundledPythonExe)) {
    throw "Publish failed: bundled Python executable was not found at $bundledPythonExe"
}

$previousDontWriteBytecode = $env:PYTHONDONTWRITEBYTECODE
$env:PYTHONDONTWRITEBYTECODE = "1"
try {
    & $bundledPythonExe -c "import sys, pandas, numpy, openpyxl, chardet, e_detection.cli; print(sys.executable)"
}
finally {
    $env:PYTHONDONTWRITEBYTECODE = $previousDontWriteBytecode
}
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed: bundled Python runtime could not import required detection modules."
}

$pythonCleanupRoots = @(
    (Join-Path $publishDir "core"),
    (Join-Path $publishDir "e_detection"),
    (Join-Path $publishDir "python-runtime")
) | Where-Object { Test-Path $_ }

Get-ChildItem -LiteralPath $pythonCleanupRoots -Recurse -Force |
    Where-Object { $_.PSIsContainer -and $_.Name -eq "__pycache__" } |
    Remove-Item -Recurse -Force

Get-ChildItem -LiteralPath $pythonCleanupRoots -Recurse -Force -Include "*.pyc", "*.pyo" |
    Remove-Item -Force

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
    "RecommendedInstaller=E-Detection.Desktop-Setup-$RuntimeIdentifier.exe"
    "InstallScript=Install-Desktop.ps1"
    "UninstallScript=Uninstall-Desktop.ps1"
    "PythonRuntime=Includes CPython $BundledPythonVersion embeddable runtime in python-runtime"
    "PythonCore=Includes local e_detection source and dependencies installed into the bundled runtime"
    "OfflineWheelhouse=Included only for advanced custom Python repair paths"
) | Set-Content -Path $infoPath -Encoding UTF8

$installTextPath = Join-Path $publishDir "INSTALL.txt"
@(
    "E-Detection Desktop"
    ""
    "Recommended for most Windows users:"
    "  Download and run E-Detection.Desktop-Setup-win-x64.exe from GitHub Releases."
    "  The setup wizard lets you choose the install location and creates normal Windows shortcuts."
    "  To update, run the newer setup wizard and follow the on-screen prompts."
    "  To uninstall, use Windows Settings > Installed apps, or run the Start Menu uninstaller."
    ""
    "Advanced portable install for the current Windows user:"
    "  powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1"
    ""
    "Advanced install without shortcuts:"
    "  powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1 -NoDesktopShortcut -NoStartMenuShortcut"
    ""
    "Advanced install to a custom location:"
    "  powershell -ExecutionPolicy Bypass -File .\Install-Desktop.ps1 -InstallDirectory ""D:\Apps\E-Detection Desktop"""
    ""
    "Advanced portable uninstall:"
    "  powershell -ExecutionPolicy Bypass -File .\Uninstall-Desktop.ps1"
    "  If this command is run inside a setup-wizard install, it delegates to the Windows setup uninstaller."
    ""
    "The desktop app includes a bundled Python runtime for ordinary users."
    "No command-line Python setup is required for the standard installer."
    "Advanced users may still point the app at a custom Python executable from Settings."
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

$smokeResultsPath = Join-Path $publishDir "smoke-results"
if (Test-Path $smokeResultsPath) {
    Remove-Item -LiteralPath $smokeResultsPath -Recurse -Force
}

& (Join-Path $publishDir "Test-DesktopPackageHealth.ps1") -PackagePath $publishDir

$smokeResultsPath = Join-Path $publishDir "smoke-results"
if (Test-Path $smokeResultsPath) {
    Remove-Item -LiteralPath $smokeResultsPath -Recurse -Force
}

Get-ChildItem -LiteralPath (Join-Path $publishDir "core"), (Join-Path $publishDir "e_detection"), (Join-Path $publishDir "python-runtime") -Recurse -Force |
    Where-Object { $_.PSIsContainer -and $_.Name -eq "__pycache__" } |
    Remove-Item -Recurse -Force

Get-ChildItem -LiteralPath (Join-Path $publishDir "core"), (Join-Path $publishDir "e_detection"), (Join-Path $publishDir "python-runtime") -Recurse -Force -Include "*.pyc", "*.pyo" |
    Remove-Item -Force

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
