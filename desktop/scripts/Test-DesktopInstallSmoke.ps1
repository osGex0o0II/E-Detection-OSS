[CmdletBinding()]
param(
    [string]$PackagePath = "",

    [string]$InstallDirectory = "",

    [switch]$IncludeShortcuts,

    [switch]$KeepInstallDirectory,

    [switch]$AsJson
)

$ErrorActionPreference = "Stop"

$productName = "EDetection"
$entryPoint = "EDetection.exe"
$shortcutName = "$productName.lnk"

function Resolve-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-SmokeInstallPath([string]$Path) {
    $full = Resolve-FullPath $Path
    $allowedToken = "E-Detection-Desktop-InstallSmoke"
    $artifactToken = [System.IO.Path]::Combine("artifacts", "desktop", "install-smoke")
    if ($full.IndexOf($allowedToken, [System.StringComparison]::OrdinalIgnoreCase) -lt 0 `
        -and $full.IndexOf($artifactToken, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Refusing to run install smoke outside a smoke directory: $full"
    }
}

function Get-AppPathsSnapshot([string]$KeyPath) {
    if (!(Test-Path $KeyPath)) {
        return [pscustomobject]@{
            Exists = $false
            DefaultValue = $null
            PathValue = $null
        }
    }

    $key = Get-Item $KeyPath
    $properties = Get-ItemProperty $KeyPath
    return [pscustomobject]@{
        Exists = $true
        DefaultValue = $key.GetValue("")
        PathValue = $properties.Path
    }
}

function Restore-AppPathsSnapshot($Snapshot, [string]$KeyPath) {
    if ($Snapshot.Exists) {
        New-Item -Path $KeyPath -Force | Out-Null
        if ($null -ne $Snapshot.DefaultValue) {
            Set-Item -Path $KeyPath -Value $Snapshot.DefaultValue
        }

        if ($null -ne $Snapshot.PathValue) {
            New-ItemProperty -Path $KeyPath -Name "Path" -Value $Snapshot.PathValue -PropertyType String -Force | Out-Null
        }
        elseif ((Get-ItemProperty $KeyPath -Name "Path" -ErrorAction SilentlyContinue)) {
            Remove-ItemProperty -Path $KeyPath -Name "Path" -Force
        }
    }
    elseif (Test-Path $KeyPath) {
        Remove-Item -Path $KeyPath -Recurse -Force
    }
}

function Get-RegistryValueSnapshot([string]$KeyPath, [string]$Name) {
    $key = Get-Item -Path $KeyPath -ErrorAction SilentlyContinue
    if ($null -eq $key) {
        return [pscustomobject]@{
            Exists = $false
            KeyExists = $false
            Name = $Name
            Value = $null
        }
    }

    $value = $key.GetValue($Name)
    return [pscustomobject]@{
        Exists = ($null -ne $value)
        KeyExists = $true
        Name = $Name
        Value = $value
    }
}

function Restore-RegistryValueSnapshot($Snapshot, [string]$KeyPath) {
    if ($Snapshot.Exists) {
        New-Item -Path $KeyPath -Force | Out-Null
        New-ItemProperty -Path $KeyPath -Name $Snapshot.Name -Value $Snapshot.Value -PropertyType String -Force | Out-Null
    }
    elseif (Test-Path $KeyPath) {
        Remove-ItemProperty -Path $KeyPath -Name $Snapshot.Name -Force -ErrorAction SilentlyContinue
    }
}

function Get-ScheduledTaskXml([string]$TaskName) {
    $output = & schtasks.exe /Query /TN $TaskName /XML 2>$null
    if ($LASTEXITCODE -ne 0) {
        $global:LASTEXITCODE = 0
        return $null
    }

    return ($output -join [Environment]::NewLine)
}

function Remove-ScheduledTaskIfExists([string]$TaskName) {
    & schtasks.exe /Delete /TN $TaskName /F 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $global:LASTEXITCODE = 0
    }
}

function Restore-ScheduledTaskXml([string]$TaskName, [string]$TaskXml) {
    if ([string]::IsNullOrWhiteSpace($TaskXml)) {
        return
    }

    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) "EDetectionDesktopTaskRestore-$([Guid]::NewGuid().ToString('N')).xml"
    try {
        Set-Content -Path $tempPath -Value $TaskXml -Encoding Unicode
        & schtasks.exe /Create /TN $TaskName /XML $tempPath /F | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to restore scheduled task '$TaskName'."
        }
    }
    finally {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
}

function New-SmokeScheduledStartupTask([string]$TaskName, [string]$ExecutablePath) {
    $taskCommand = "`"$ExecutablePath`" --background-startup"
    & schtasks.exe /Create /TN $TaskName /SC ONLOGON /TR $taskCommand /F 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $global:LASTEXITCODE = 0
        return $false
    }

    return $true
}

function Save-ShortcutSnapshot([string]$ShortcutPath, [string]$SnapshotRoot, [string]$SnapshotName) {
    $name = if ([string]::IsNullOrWhiteSpace($SnapshotName)) {
        [System.IO.Path]::GetFileName($ShortcutPath)
    }
    else {
        $SnapshotName
    }
    $snapshotPath = Join-Path $SnapshotRoot $name
    if (Test-Path $ShortcutPath) {
        New-Item -ItemType Directory -Force -Path $SnapshotRoot | Out-Null
        Copy-Item -LiteralPath $ShortcutPath -Destination $snapshotPath -Force
        return [pscustomobject]@{
            Exists = $true
            Path = $ShortcutPath
            SnapshotPath = $snapshotPath
        }
    }

    return [pscustomobject]@{
        Exists = $false
        Path = $ShortcutPath
        SnapshotPath = $snapshotPath
    }
}

function Restore-ShortcutSnapshot($Snapshot) {
    if ($Snapshot.Exists) {
        $parent = Split-Path -Parent $Snapshot.Path
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        Copy-Item -LiteralPath $Snapshot.SnapshotPath -Destination $Snapshot.Path -Force
    }
    elseif (Test-Path $Snapshot.Path) {
        Remove-Item -LiteralPath $Snapshot.Path -Force
    }
}

function Invoke-SmokePackageHealth([string]$ScriptPath, [string]$Path) {
    if ($AsJson) {
        & $ScriptPath -PackagePath $Path -AsJson | Out-Null
    }
    else {
        & $ScriptPath -PackagePath $Path | Out-Host
    }
}

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$packageFull = Resolve-FullPath ((Resolve-Path $PackagePath).Path)

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "E-Detection-Desktop-InstallSmoke\app"
}

$installFull = Resolve-FullPath $InstallDirectory
Assert-SmokeInstallPath $installFull

$healthScript = Join-Path $packageFull "Test-DesktopPackageHealth.ps1"
$installScript = Join-Path $packageFull "Install-Desktop.ps1"
if (!(Test-Path $healthScript)) {
    throw "Install smoke failed: Test-DesktopPackageHealth.ps1 was not found in $packageFull"
}

if (!(Test-Path $installScript)) {
    throw "Install smoke failed: Install-Desktop.ps1 was not found in $packageFull"
}

$smokeRoot = Split-Path -Parent $installFull
$snapshotRoot = Join-Path $smokeRoot "snapshots"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) $shortcutName
$startMenuFolder = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) "E-Detection"
$startMenuShortcut = Join-Path $startMenuFolder $shortcutName
$appPathsKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$entryPoint"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$startupEntryName = "EDetection"
$startupTaskName = "EDetection Autostart"
$appPathsSnapshot = Get-AppPathsSnapshot $appPathsKey
$startupSnapshot = Get-RegistryValueSnapshot $runKey $startupEntryName
$startupTaskSnapshot = Get-ScheduledTaskXml $startupTaskName
$desktopShortcutSnapshot = Save-ShortcutSnapshot $desktopShortcut $snapshotRoot "desktop-shortcut.lnk"
$startMenuShortcutSnapshot = Save-ShortcutSnapshot $startMenuShortcut $snapshotRoot "start-menu-shortcut.lnk"

$result = [ordered]@{
    PackagePath = $packageFull
    DetectionBackend = "native"
    InstallDirectory = $installFull
    IncludeShortcuts = [bool]$IncludeShortcuts
    PackageHealthPassed = $false
    InstallPassed = $false
    InstalledPackageHealthPassed = $false
    AppPathsVerified = $false
    StartupEntryRemoved = $false
    StartupTaskRemoved = $false
    StartupTaskCreationAvailable = $false
    UserFilePreserved = $false
    ShortcutsVerified = !$IncludeShortcuts
    UninstallPassed = $false
    UserArtifactsRestored = $false
    CheckedAt = (Get-Date).ToString("o")
}

$installed = $false

try {
    Invoke-SmokePackageHealth $healthScript $packageFull
    $result.PackageHealthPassed = $true

    if ($IncludeShortcuts) {
        & $installScript -SourcePath $packageFull -InstallDirectory $installFull -Quiet:$AsJson
    }
    else {
        & $installScript `
            -SourcePath $packageFull `
            -InstallDirectory $installFull `
            -NoDesktopShortcut `
            -NoStartMenuShortcut `
            -Quiet:$AsJson
    }
    $installed = $true

    $installedExe = Join-Path $installFull $entryPoint
    if (!(Test-Path $installedExe)) {
        throw "Install smoke failed: installed executable was not found at $installedExe"
    }

    $result.InstallPassed = $true

    $installedHealthScript = Join-Path $installFull "Test-DesktopPackageHealth.ps1"
    if (!(Test-Path $installedHealthScript)) {
        throw "Install smoke failed: installed package health script was not found at $installedHealthScript"
    }

    Invoke-SmokePackageHealth $installedHealthScript $installFull
    $result.InstalledPackageHealthPassed = $true

    if (!(Test-Path $appPathsKey)) {
        throw "Install smoke failed: App Paths entry was not created at $appPathsKey"
    }

    $appPathsDefault = (Get-Item $appPathsKey).GetValue("")
    $appPathsPath = (Get-ItemProperty $appPathsKey).Path
    if (![string]::Equals($appPathsDefault, $installedExe, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Install smoke failed: App Paths default value was '$appPathsDefault', expected '$installedExe'"
    }

    if (![string]::Equals($appPathsPath, $installFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Install smoke failed: App Paths Path value was '$appPathsPath', expected '$installFull'"
    }

    $result.AppPathsVerified = $true

    $sentinelPath = Join-Path $installFull "user-report-sentinel.txt"
    Set-Content -Path $sentinelPath -Value "user-owned file" -Encoding UTF8

    New-Item -Path $runKey -Force | Out-Null
    $startupValue = "`"$installedExe`" --background-startup"
    New-ItemProperty -Path $runKey -Name $startupEntryName -Value $startupValue -PropertyType String -Force | Out-Null
    Remove-ScheduledTaskIfExists $startupTaskName
    $result.StartupTaskCreationAvailable = New-SmokeScheduledStartupTask $startupTaskName $installedExe

    if ($IncludeShortcuts) {
        if (!(Test-Path $desktopShortcut)) {
            throw "Install smoke failed: Desktop shortcut was not created at $desktopShortcut"
        }

        if (!(Test-Path $startMenuShortcut)) {
            throw "Install smoke failed: Start Menu shortcut was not created at $startMenuShortcut"
        }

        $result.ShortcutsVerified = $true
    }

    $uninstallScript = Join-Path $installFull "Uninstall-Desktop.ps1"
    if (!(Test-Path $uninstallScript)) {
        throw "Install smoke failed: installed uninstall script was not found at $uninstallScript"
    }

    & $uninstallScript -InstallDirectory $installFull -Quiet:$AsJson
    $installed = $false

    if (Test-Path $installedExe) {
        throw "Install smoke failed: installed executable still exists after uninstall: $installedExe"
    }

    if (!(Test-Path $sentinelPath)) {
        throw "Install smoke failed: user-owned sentinel file was not preserved after uninstall: $sentinelPath"
    }

    $result.UserFilePreserved = $true

    if (Test-Path $appPathsKey) {
        throw "Install smoke failed: App Paths entry still exists after uninstall: $appPathsKey"
    }

    $runKeyAfterUninstall = Get-Item -Path $runKey -ErrorAction SilentlyContinue
    $startupAfterUninstall = if ($null -ne $runKeyAfterUninstall) {
        $runKeyAfterUninstall.GetValue($startupEntryName)
    }
    else {
        $null
    }
    $startupEntryTargetsInstall = $startupAfterUninstall -is [string] `
        -and $startupAfterUninstall.IndexOf($installedExe, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    if ($startupEntryTargetsInstall) {
        throw "Install smoke failed: startup entry still points at installed executable: $startupAfterUninstall"
    }

    $result.StartupEntryRemoved = $true

    if ($result.StartupTaskCreationAvailable) {
        $taskAfterUninstall = Get-ScheduledTaskXml $startupTaskName
        $startupTaskTargetsInstall = ![string]::IsNullOrWhiteSpace($taskAfterUninstall) `
            -and $taskAfterUninstall.IndexOf($installedExe, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        if ($startupTaskTargetsInstall) {
            throw "Install smoke failed: scheduled startup task still points at installed executable."
        }
    }

    $result.StartupTaskRemoved = $true

    if ($IncludeShortcuts) {
        if (Test-Path $desktopShortcut) {
            throw "Install smoke failed: Desktop shortcut still exists after uninstall: $desktopShortcut"
        }

        if (Test-Path $startMenuShortcut) {
            throw "Install smoke failed: Start Menu shortcut still exists after uninstall: $startMenuShortcut"
        }
    }

    $result.UninstallPassed = $true
}
finally {
    try {
        if ($installed -and !$KeepInstallDirectory) {
            $cleanupUninstall = Join-Path $installFull "Uninstall-Desktop.ps1"
            if (Test-Path $cleanupUninstall) {
                & $cleanupUninstall -InstallDirectory $installFull -Quiet:$AsJson
            }
        }
    }
    finally {
        Restore-AppPathsSnapshot $appPathsSnapshot $appPathsKey
        Restore-RegistryValueSnapshot $startupSnapshot $runKey
        Remove-ScheduledTaskIfExists $startupTaskName
        Restore-ScheduledTaskXml $startupTaskName $startupTaskSnapshot
        Restore-ShortcutSnapshot $desktopShortcutSnapshot
        Restore-ShortcutSnapshot $startMenuShortcutSnapshot
        $result.UserArtifactsRestored = $true

        if (!$KeepInstallDirectory -and (Test-Path $smokeRoot)) {
            Assert-SmokeInstallPath $smokeRoot
            Remove-Item -LiteralPath $smokeRoot -Recurse -Force
        }
    }
}

$result.Passed = $result.PackageHealthPassed `
    -and $result.InstallPassed `
    -and $result.InstalledPackageHealthPassed `
    -and $result.AppPathsVerified `
    -and $result.StartupEntryRemoved `
    -and $result.StartupTaskRemoved `
    -and $result.UserFilePreserved `
    -and $result.ShortcutsVerified `
    -and $result.UninstallPassed `
    -and $result.UserArtifactsRestored

$output = [pscustomobject]$result
if ($AsJson) {
    $output | ConvertTo-Json
}

if (!$result.Passed) {
    throw "Install smoke failed."
}

if (!$AsJson) {
    Write-Host "Install smoke passed: $packageFull"
}

$global:LASTEXITCODE = 0
exit 0
