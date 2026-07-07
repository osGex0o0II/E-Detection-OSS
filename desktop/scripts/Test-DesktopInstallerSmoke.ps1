param(
    [string]$InstallerPath = "",

    [string]$InstallDirectory = "",

    [string]$UnsafeInstallDirectory = "",

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
$updateLog = Join-Path $smokeRoot "update.log"
$unsafeInstallLog = Join-Path $smokeRoot "unsafe-install.log"
$uninstallLog = Join-Path $smokeRoot "uninstall.log"
$entryPoint = "EDetection.Desktop.exe"
$installedExe = Join-Path $installFull $entryPoint
$appPathsKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$entryPoint"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$startupEntryName = "E-Detection Desktop"
$startupTaskName = "E-Detection Desktop Autostart"
$startupSnapshot = Get-RegistryValueSnapshot $runKey $startupEntryName
$startupTaskSnapshot = Get-ScheduledTaskXml $startupTaskName
$settingsDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "E-Detection\Desktop"
$settingsPath = Join-Path $settingsDirectory "settings.json"
$settingsExisted = Test-Path $settingsPath
$settingsBackup = if ($settingsExisted) {
    Get-Content -Path $settingsPath -Raw
}
else {
    $null
}

try {
    if (Test-Path $installFull) {
        Remove-Item -LiteralPath $installFull -Recurse -Force
    }

    if (![string]::IsNullOrWhiteSpace($UnsafeInstallDirectory)) {
        $unsafeFull = Resolve-FullPath $UnsafeInstallDirectory
        Assert-SmokeInstallPath $unsafeFull
        New-Item -ItemType Directory -Force -Path $unsafeFull | Out-Null
        $unsafeProcess = Start-Process `
            -FilePath $installerFull `
            -ArgumentList @(
                "/VERYSILENT",
                "/SUPPRESSMSGBOXES",
                "/NORESTART",
                "/SP-",
                "/DIR=$unsafeFull",
                "/LOG=$unsafeInstallLog"
            ) `
            -Wait `
            -PassThru `
            -NoNewWindow
        if ($unsafeProcess.ExitCode -eq 0) {
            throw "Installer smoke failed: unsafe install directory was accepted: $unsafeFull"
        }
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

    if (!(Test-Path $appPathsKey)) {
        throw "Installer smoke failed: App Paths entry was not created at $appPathsKey"
    }

    $healthScript = Join-Path $installFull "Test-DesktopPackageHealth.ps1"
    if (!(Test-Path $healthScript)) {
        throw "Installer smoke failed: package health script was not installed at $healthScript"
    }

    & $healthScript -PackagePath $installFull

    New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
    $settingsMarker = "installer-update-smoke-$([Guid]::NewGuid().ToString('N'))"
    [pscustomobject]@{
        SettingsVersion = 8
        InstallerUpdateSmokeMarker = $settingsMarker
    } | ConvertTo-Json | Set-Content -Path $settingsPath -Encoding UTF8

    $staleTopLevelFile = Join-Path $installFull "obsolete-publish-file.txt"
    $staleRuntimeDirectory = Join-Path $installFull "python-runtime\obsolete-package"
    New-Item -ItemType Directory -Force -Path $staleRuntimeDirectory | Out-Null
    Set-Content -Path $staleTopLevelFile -Value "stale top-level file" -Encoding ASCII
    Set-Content -Path (Join-Path $staleRuntimeDirectory "stale.txt") -Value "stale runtime file" -Encoding ASCII

    Set-Content -Path $installedExe -Value "corrupted by installer update smoke" -Encoding ASCII
    Invoke-Native `
        -FilePath $installerFull `
        -Arguments @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/SP-",
            "/DIR=$installFull",
            "/LOG=$updateLog"
        ) `
        -FailureMessage "Installer smoke failed: update/repair setup did not complete successfully."

    & $healthScript -PackagePath $installFull
    if (Test-Path $staleTopLevelFile) {
        throw "Installer smoke failed: stale top-level file was not removed during update: $staleTopLevelFile"
    }

    if (Test-Path $staleRuntimeDirectory) {
        throw "Installer smoke failed: stale runtime directory was not removed during update: $staleRuntimeDirectory"
    }

    $settingsAfterUpdate = Get-Content -Path $settingsPath -Raw | ConvertFrom-Json
    if ($settingsAfterUpdate.InstallerUpdateSmokeMarker -ne $settingsMarker) {
        throw "Installer smoke failed: user settings were not preserved during update/repair install."
    }

    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name $startupEntryName -Value "`"$installedExe`" --background-startup" -PropertyType String -Force | Out-Null
    Remove-ScheduledTaskIfExists $startupTaskName
    $startupTaskCreated = New-SmokeScheduledStartupTask $startupTaskName $installedExe

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

    if (Test-Path $appPathsKey) {
        throw "Installer smoke failed: App Paths entry still exists after uninstall: $appPathsKey"
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
        throw "Installer smoke failed: startup entry still points at installed executable: $startupAfterUninstall"
    }

    if ($startupTaskCreated) {
        $taskAfterUninstall = Get-ScheduledTaskXml $startupTaskName
        $startupTaskTargetsInstall = ![string]::IsNullOrWhiteSpace($taskAfterUninstall) `
            -and $taskAfterUninstall.IndexOf($installedExe, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        if ($startupTaskTargetsInstall) {
            throw "Installer smoke failed: scheduled startup task still points at installed executable."
        }
    }
}
finally {
    Restore-RegistryValueSnapshot $startupSnapshot $runKey
    Remove-ScheduledTaskIfExists $startupTaskName
    Restore-ScheduledTaskXml $startupTaskName $startupTaskSnapshot

    if ($settingsExisted) {
        New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
        Set-Content -Path $settingsPath -Value $settingsBackup -Encoding UTF8
    }
    else {
        Remove-Item -LiteralPath $settingsPath -Force -ErrorAction SilentlyContinue
    }

    if (!$KeepInstallDirectory -and (Test-Path $smokeRoot)) {
        Assert-SmokeInstallPath $smokeRoot
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Installer smoke passed: $installerFull"
$global:LASTEXITCODE = 0
exit 0
