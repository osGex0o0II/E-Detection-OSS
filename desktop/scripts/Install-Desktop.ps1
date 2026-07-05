[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SourcePath = "",

    [string]$InstallDirectory = "",

    [switch]$NoDesktopShortcut,

    [switch]$NoStartMenuShortcut,

    [switch]$Launch,

    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$productName = "E-Detection Desktop"
$entryPoint = "EDetection.Desktop.exe"
$shortcutName = "$productName.lnk"

function Resolve-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function New-AppShortcut(
    [string]$ShortcutPath,
    [string]$TargetPath,
    [string]$WorkingDirectory,
    [string]$IconPath
) {
    $shortcutParent = Split-Path -Parent $ShortcutPath
    New-Item -ItemType Directory -Force -Path $shortcutParent | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = $IconPath
    $shortcut.Description = "Windows native workbench for E-Detection"
    $shortcut.Save()
}

function Invoke-PackageHealth([string]$Path) {
    $healthScript = Join-Path $Path "Test-DesktopPackageHealth.ps1"
    if (!(Test-Path $healthScript)) {
        throw "Install failed: Test-DesktopPackageHealth.ps1 was not found in $Path"
    }

    if ($Quiet) {
        & $healthScript -PackagePath $Path -AsJson | Out-Null
    }
    else {
        & $healthScript -PackagePath $Path | Out-Host
    }
}

if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $env:LOCALAPPDATA "Programs\E-Detection Desktop"
}

$sourceFull = Resolve-FullPath ((Resolve-Path $SourcePath).Path)
$installFull = Resolve-FullPath $InstallDirectory
$sourceExe = Join-Path $sourceFull $entryPoint

if (!(Test-Path $sourceExe)) {
    throw "Install failed: $entryPoint was not found in $sourceFull"
}

Invoke-PackageHealth $sourceFull

$installInPlace = [string]::Equals($sourceFull, $installFull, [System.StringComparison]::OrdinalIgnoreCase)

if (!$installInPlace) {
    $installParent = Split-Path -Parent $installFull
    New-Item -ItemType Directory -Force -Path $installParent | Out-Null

    if ($PSCmdlet.ShouldProcess($installFull, "Install $productName")) {
        if (Test-Path $installFull) {
            Remove-Item -LiteralPath $installFull -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $installFull | Out-Null
        Copy-Item -Path (Join-Path $sourceFull "*") -Destination $installFull -Recurse -Force
    }
}

$installedExe = Join-Path $installFull $entryPoint
$installedIcon = Join-Path $installFull "Assets\Icons\app.ico"
$programsPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$startMenuShortcut = Join-Path $programsPath "E-Detection\$shortcutName"
$desktopPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$desktopShortcut = Join-Path $desktopPath $shortcutName

if (!$NoStartMenuShortcut) {
    if ($PSCmdlet.ShouldProcess($startMenuShortcut, "Create Start Menu shortcut")) {
        New-AppShortcut $startMenuShortcut $installedExe $installFull $installedIcon
    }
}

if (!$NoDesktopShortcut) {
    if ($PSCmdlet.ShouldProcess($desktopShortcut, "Create Desktop shortcut")) {
        New-AppShortcut $desktopShortcut $installedExe $installFull $installedIcon
    }
}

$appPathsKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$entryPoint"
if ($PSCmdlet.ShouldProcess($appPathsKey, "Register App Paths entry")) {
    New-Item -Path $appPathsKey -Force | Out-Null
    Set-Item -Path $appPathsKey -Value $installedExe
    New-ItemProperty -Path $appPathsKey -Name "Path" -Value $installFull -PropertyType String -Force | Out-Null
}

if (!$WhatIfPreference) {
    Invoke-PackageHealth $installFull

    if (!(Test-Path $installedExe)) {
        throw "Install failed: installed executable was not found at $installedExe"
    }

    if (!$NoStartMenuShortcut -and !(Test-Path $startMenuShortcut)) {
        throw "Install failed: Start Menu shortcut was not created at $startMenuShortcut"
    }

    if (!$NoDesktopShortcut -and !(Test-Path $desktopShortcut)) {
        throw "Install failed: Desktop shortcut was not created at $desktopShortcut"
    }

    if (!(Test-Path $appPathsKey)) {
        throw "Install failed: App Paths entry was not created at $appPathsKey"
    }
}

if ($WhatIfPreference) {
    if (!$Quiet) {
        Write-Host "$productName install plan validated for $installFull"
    }
}
else {
    if (!$Quiet) {
        Write-Host "$productName installed to $installFull"
    }
}

if ($Launch) {
    if ($PSCmdlet.ShouldProcess($installedExe, "Launch $productName")) {
        Start-Process -FilePath $installedExe -WorkingDirectory $installFull
    }
}
