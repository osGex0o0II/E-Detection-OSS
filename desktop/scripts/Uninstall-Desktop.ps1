[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallDirectory = "",

    [switch]$RemoveSettings,

    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$productName = "E-Detection Desktop"
$entryPoint = "EDetection.Desktop.exe"
$shortcutName = "$productName.lnk"

function Resolve-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
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

foreach ($shortcut in @($desktopShortcut, $startMenuShortcut)) {
    if (Test-Path $shortcut) {
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

if (Test-Path $appPathsKey) {
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

if (Test-Path $installFull) {
    $currentLocation = Resolve-FullPath (Get-Location).Path
    if ($currentLocation.StartsWith($installFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        Set-Location $env:TEMP
    }

    if ($PSCmdlet.ShouldProcess($installFull, "Remove installed application files")) {
        Remove-Item -LiteralPath $installFull -Recurse -Force
    }
}

if ($RemoveSettings -and (Test-Path $settingsDirectory)) {
    if ($PSCmdlet.ShouldProcess($settingsDirectory, "Remove user settings")) {
        Remove-Item -LiteralPath $settingsDirectory -Recurse -Force
    }
}

if (!$WhatIfPreference) {
    $remaining = @()
    foreach ($path in @($desktopShortcut, $startMenuShortcut, $installFull)) {
        if (Test-Path $path) {
            $remaining += $path
        }
    }

    if (Test-Path $appPathsKey) {
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
        Write-Host "$productName uninstalled from $installFull"
    }
}

if (!$RemoveSettings -and !$Quiet) {
    Write-Host "User settings were kept at $settingsDirectory"
}
