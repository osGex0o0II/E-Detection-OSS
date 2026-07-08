[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallDirectory = "",

    [switch]$RemoveSettings,

    [switch]$CleanupOnly,

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
            throw "Uninstall failed: refusing to use unsafe install directory '$full'."
        }
    }
}

function Test-ProductDirectory([string]$Path) {
    return (Test-Path (Join-Path $Path $entryPoint)) -or (Test-Path (Join-Path $Path "release-info.txt"))
}

function Remove-EmptyDirectories([string]$Path) {
    if (!(Test-Path $Path)) {
        return
    }

    Get-ChildItem -LiteralPath $Path -Directory -Recurse -Force |
        Sort-Object FullName -Descending |
        ForEach-Object {
            if (-not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) {
                Remove-Item -LiteralPath $_.FullName -Force
            }
        }

    if (-not (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $Path -Force
    }
}

function Remove-InstalledFiles([string]$Path) {
    if (!(Test-Path $Path)) {
        return
    }

    if (!(Test-ProductDirectory $Path)) {
        throw "Uninstall failed: '$Path' does not look like an E-Detection Desktop install directory."
    }

    $rootFull = Resolve-FullPath $Path
    $manifestPath = Join-Path $Path $installManifestName
    if (!(Test-Path $manifestPath)) {
        throw "Uninstall failed: install manifest was not found at $manifestPath. Refusing to delete the whole directory."
    }

    $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    foreach ($relativePath in @($manifest.Files)) {
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            continue
        }

        if ([System.IO.Path]::IsPathRooted($relativePath)) {
            throw "Uninstall failed: manifest entry must be relative: $relativePath"
        }

        $target = Resolve-FullPath (Join-Path $Path $relativePath)
        if (!(Test-PathInsideDirectory $target $rootFull)) {
            throw "Uninstall failed: manifest entry escapes install directory: $relativePath"
        }

        if (Test-Path $target) {
            Remove-Item -LiteralPath $target -Force
        }
    }

    Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
    Remove-EmptyDirectories $Path
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $env:LOCALAPPDATA "Programs\E-Detection Desktop"
}

$installFull = Resolve-FullPath $InstallDirectory
$desktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) $shortcutName
$startMenuFolder = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) "E-Detection"
$startMenuShortcut = Join-Path $startMenuFolder $shortcutName
$appPathsKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$entryPoint"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$startupEntryName = "E-Detection Desktop"
$startupTaskName = "E-Detection Desktop Autostart"
$settingsDirectory = Join-Path $env:LOCALAPPDATA "E-Detection\Desktop"
Assert-SafeInstallDirectory $installFull

function Get-StartupTaskXml {
    $output = & schtasks.exe /Query /TN $startupTaskName /XML 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return ($output -join [Environment]::NewLine)
}

function Test-StartupTaskTargetsInstall {
    $xml = Get-StartupTaskXml
    return ![string]::IsNullOrWhiteSpace($xml) `
        -and $xml.IndexOf($installFull, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-AppPathsTargetsInstall {
    $key = Get-Item -Path $appPathsKey -ErrorAction SilentlyContinue
    if ($null -eq $key) {
        return $false
    }

    $registeredExe = $key.GetValue("")
    $registeredPath = (Get-ItemProperty -Path $appPathsKey -Name "Path" -ErrorAction SilentlyContinue).Path
    $installedExe = Join-Path $installFull $entryPoint
    $exeMatches = $registeredExe -is [string] `
        -and [string]::Equals(
            (Resolve-FullPath $registeredExe),
            (Resolve-FullPath $installedExe),
            [System.StringComparison]::OrdinalIgnoreCase)
    $pathMatches = $registeredPath -is [string] `
        -and [string]::Equals(
            (Resolve-FullPath $registeredPath),
            $installFull,
            [System.StringComparison]::OrdinalIgnoreCase)
    return $exeMatches -or $pathMatches
}

function Test-ShortcutTargetsInstall([string]$ShortcutPath) {
    if (!(Test-Path $ShortcutPath)) {
        return $false
    }

    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $targetPath = $shortcut.TargetPath
        if ([string]::IsNullOrWhiteSpace($targetPath)) {
            return $false
        }

        return Test-PathInsideDirectory (Resolve-FullPath $targetPath) $installFull
    }
    catch {
        return $false
    }
}

function Invoke-InnoUninstallerIfAvailable {
    if ($CleanupOnly) {
        return $false
    }

    $manifestPath = Join-Path $installFull $installManifestName
    if (Test-Path $manifestPath) {
        return $false
    }

    $innoUninstaller = Join-Path $installFull "unins000.exe"
    if (!(Test-Path $innoUninstaller)) {
        return $false
    }

    if (!$Quiet) {
        Write-Host "Delegating uninstall to Windows setup uninstaller at $innoUninstaller"
    }

    $arguments = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART")
    $process = Start-Process `
        -FilePath $innoUninstaller `
        -ArgumentList $arguments `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Uninstall failed: Windows setup uninstaller exited with code $($process.ExitCode)."
    }

    return $true
}

if (Invoke-InnoUninstallerIfAvailable) {
    return
}

foreach ($shortcut in @($desktopShortcut, $startMenuShortcut)) {
    if (Test-ShortcutTargetsInstall $shortcut) {
        if ($PSCmdlet.ShouldProcess($shortcut, "Remove shortcut")) {
            Remove-Item -LiteralPath $shortcut -Force
        }
    }
}

if ((Test-Path $startMenuFolder) -and -not (Get-ChildItem -LiteralPath $startMenuFolder -Force | Select-Object -First 1)) {
    if ($PSCmdlet.ShouldProcess($startMenuFolder, "Remove empty Start Menu folder")) {
        Remove-Item -LiteralPath $startMenuFolder -Force
    }
}

if (Test-AppPathsTargetsInstall) {
    if ($PSCmdlet.ShouldProcess($appPathsKey, "Remove App Paths entry")) {
        Remove-Item -Path $appPathsKey -Recurse -Force
    }
}

$runKeyItem = Get-Item -Path $runKey -ErrorAction SilentlyContinue
$startupValue = if ($null -ne $runKeyItem) {
    $runKeyItem.GetValue($startupEntryName)
}
else {
    $null
}
$startupEntryTargetsInstall = $startupValue -is [string] `
    -and $startupValue.IndexOf($installFull, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
if ($startupEntryTargetsInstall) {
    if ($PSCmdlet.ShouldProcess($runKey, "Remove startup entry")) {
        Remove-ItemProperty -Path $runKey -Name $startupEntryName -Force
    }
}

if (Test-StartupTaskTargetsInstall) {
    if ($PSCmdlet.ShouldProcess($startupTaskName, "Remove scheduled startup task")) {
        & schtasks.exe /Delete /TN $startupTaskName /F | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to remove scheduled startup task '$startupTaskName'."
        }
    }
}

if (!$CleanupOnly -and (Test-Path $installFull)) {
    $currentLocation = Resolve-FullPath (Get-Location).Path
    if (Test-PathInsideDirectory $currentLocation $installFull) {
        Set-Location $env:TEMP
    }

    if ($PSCmdlet.ShouldProcess($installFull, "Remove installed application files")) {
        Remove-InstalledFiles $installFull
    }
}

if ($RemoveSettings -and (Test-Path $settingsDirectory)) {
    if ($PSCmdlet.ShouldProcess($settingsDirectory, "Remove user settings")) {
        Remove-Item -LiteralPath $settingsDirectory -Recurse -Force
    }
}

if (!$WhatIfPreference) {
    $remaining = @()
    foreach ($path in @($desktopShortcut, $startMenuShortcut)) {
        if (Test-ShortcutTargetsInstall $path) {
            $remaining += $path
        }
    }

    $installedExe = Join-Path $installFull $entryPoint
    if (Test-Path $installedExe) {
        $remaining += $installedExe
    }

    if (Test-AppPathsTargetsInstall) {
        $remaining += $appPathsKey
    }

    $runKeyItem = Get-Item -Path $runKey -ErrorAction SilentlyContinue
    $startupValue = if ($null -ne $runKeyItem) {
        $runKeyItem.GetValue($startupEntryName)
    }
    else {
        $null
    }
    $startupEntryTargetsInstall = $startupValue -is [string] `
        -and $startupValue.IndexOf($installFull, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    if ($startupEntryTargetsInstall) {
        $remaining += "$runKey\$startupEntryName"
    }

    if (Test-StartupTaskTargetsInstall) {
        $remaining += "ScheduledTask:$startupTaskName"
    }

    if ($RemoveSettings -and (Test-Path $settingsDirectory)) {
        $remaining += $settingsDirectory
    }

    if ($remaining.Count -gt 0) {
        throw "Uninstall failed: remaining artifacts $($remaining -join ', ')"
    }
}

if ($WhatIfPreference) {
    if (!$Quiet) {
        Write-Host "$productName uninstall plan validated for $installFull"
    }
}
else {
    if (!$Quiet) {
        if ($CleanupOnly) {
            Write-Host "$productName cleanup completed for $installFull"
        }
        else {
            Write-Host "$productName uninstalled from $installFull"
        }
    }
}

if (!$RemoveSettings -and !$Quiet) {
    Write-Host "User settings were kept at $settingsDirectory"
}
