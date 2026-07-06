param(
    [string]$InstallerPath = "",

    [string]$InstallDirectory = "",

    [switch]$KeepInstallDirectory
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-SmokeInstallPath([string]$Path) {
    $full = Resolve-FullPath $Path
    $allowedToken = "E-Detection-Desktop-InstallerSmoke"
    $artifactToken = [System.IO.Path]::Combine("artifacts", "desktop", "installer-smoke")
    if ($full.IndexOf($allowedToken, [System.StringComparison]::OrdinalIgnoreCase) -lt 0 `
        -and $full.IndexOf($artifactToken, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Refusing to run installer smoke outside a smoke directory: $full"
    }
}

function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$FailureMessage
    )

    $process = Start-Process `
        -FilePath $FilePath `
        -ArgumentList $Arguments `
        -Wait `
        -PassThru `
        -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "$FailureMessage ExitCode=$($process.ExitCode)"
    }
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $repoRoot "artifacts\desktop\win-x64\installer\E-Detection.Desktop-Setup-win-x64.exe"
}

$installerFull = Resolve-FullPath ((Resolve-Path $InstallerPath).Path)

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "E-Detection-Desktop-InstallerSmoke\app"
}

$installFull = Resolve-FullPath $InstallDirectory
Assert-SmokeInstallPath $installFull

$smokeRoot = Split-Path -Parent $installFull
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

$installLog = Join-Path $smokeRoot "install.log"
$uninstallLog = Join-Path $smokeRoot "uninstall.log"
$entryPoint = "EDetection.Desktop.exe"
$installedExe = Join-Path $installFull $entryPoint

try {
    if (Test-Path $installFull) {
        Remove-Item -LiteralPath $installFull -Recurse -Force
    }

    Invoke-Native `
        -FilePath $installerFull `
        -Arguments @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/SP-",
            "/DIR=$installFull",
            "/LOG=$installLog"
        ) `
        -FailureMessage "Installer smoke failed: setup did not complete successfully."

    if (!(Test-Path $installedExe)) {
        throw "Installer smoke failed: installed executable was not found at $installedExe"
    }

    $healthScript = Join-Path $installFull "Test-DesktopPackageHealth.ps1"
    if (!(Test-Path $healthScript)) {
        throw "Installer smoke failed: package health script was not installed at $healthScript"
    }

    & $healthScript -PackagePath $installFull

    $uninstaller = Join-Path $installFull "unins000.exe"
    if (!(Test-Path $uninstaller)) {
        throw "Installer smoke failed: Inno uninstaller was not found at $uninstaller"
    }

    Invoke-Native `
        -FilePath $uninstaller `
        -Arguments @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/LOG=$uninstallLog"
        ) `
        -FailureMessage "Installer smoke failed: uninstall did not complete successfully."

    if (Test-Path $installedExe) {
        throw "Installer smoke failed: installed executable still exists after uninstall: $installedExe"
    }
}
finally {
    if (!$KeepInstallDirectory -and (Test-Path $smokeRoot)) {
        Assert-SmokeInstallPath $smokeRoot
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Installer smoke passed: $installerFull"
$global:LASTEXITCODE = 0
exit 0
