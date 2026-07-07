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
$installManifestName = "install-manifest.json"

function Resolve-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Test-PathInsideDirectory([string]$CandidatePath, [string]$RootPath) {
    $candidateFull = Resolve-FullPath $CandidatePath
    $rootFull = Resolve-FullPath $RootPath
    $rootTrimmed = $rootFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $rootWithSeparator = $rootTrimmed + [System.IO.Path]::DirectorySeparatorChar
    return [string]::Equals($candidateFull, $rootTrimmed, [System.StringComparison]::OrdinalIgnoreCase) `
        -or $candidateFull.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)
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

function Assert-SafeInstallDirectory([string]$Path) {
    $full = Resolve-FullPath $Path
    $root = [System.IO.Path]::GetPathRoot($full)
    $blocked = @(
        $root,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs),
        $env:LOCALAPPDATA,
        $env:APPDATA,
        $env:TEMP
    ) | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Resolve-FullPath $_ }

    foreach ($blockedPath in $blocked) {
        if ([string]::Equals($full.TrimEnd('\'), $blockedPath.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Install failed: refusing to use unsafe install directory '$full'. Choose a dedicated application folder."
        }
    }
}

function Test-ProductDirectory([string]$Path) {
    return (Test-Path (Join-Path $Path $entryPoint)) -or (Test-Path (Join-Path $Path "release-info.txt"))
}

function Get-RelativePackageFiles([string]$Path) {
    $rootFull = Resolve-FullPath $Path
    Get-ChildItem -LiteralPath $rootFull -File -Recurse -Force |
        ForEach-Object {
            [System.IO.Path]::GetRelativePath($rootFull, $_.FullName)
        } |
        Sort-Object
}

function Remove-PreviousInstallFiles([string]$Path) {
    $rootFull = Resolve-FullPath $Path
    $manifestPath = Join-Path $Path $installManifestName
    if (!(Test-Path $manifestPath)) {
        return
    }

    $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    foreach ($relativePath in @($manifest.Files)) {
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            continue
        }

        if ([System.IO.Path]::IsPathRooted($relativePath)) {
            throw "Install failed: manifest entry must be relative: $relativePath"
        }

        $target = Resolve-FullPath (Join-Path $Path $relativePath)
        if (!(Test-PathInsideDirectory $target $rootFull)) {
            throw "Install failed: manifest entry escapes install directory: $relativePath"
        }

        if (Test-Path $target) {
            Remove-Item -LiteralPath $target -Force
        }
    }
}

function Write-InstallManifest([string]$Path, [string[]]$Files) {
    $manifestPath = Join-Path $Path $installManifestName
    [pscustomobject]@{
        Product = $productName
        EntryPoint = $entryPoint
        InstalledAt = (Get-Date).ToString("o")
        Files = @($Files | Where-Object { $_ -ne $installManifestName } | Sort-Object)
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestPath -Encoding UTF8
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
Assert-SafeInstallDirectory $installFull

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
            $hasExistingFiles = $null -ne (Get-ChildItem -LiteralPath $installFull -Force | Select-Object -First 1)
            $looksLikeProductDirectory = Test-ProductDirectory $installFull
            if ($hasExistingFiles -and !$looksLikeProductDirectory) {
                throw "Install failed: '$installFull' already exists and does not look like an E-Detection Desktop install directory."
            }

            Remove-PreviousInstallFiles $installFull
        }

        New-Item -ItemType Directory -Force -Path $installFull | Out-Null
        Copy-Item -Path (Join-Path $sourceFull "*") -Destination $installFull -Recurse -Force
        Write-InstallManifest $installFull @(Get-RelativePackageFiles $sourceFull)
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
