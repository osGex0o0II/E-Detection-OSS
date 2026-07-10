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
    try {
        $output = & schtasks.exe /Query /TN $TaskName /XML 2>$null
    }
    catch {
        $global:LASTEXITCODE = 0
        return $null
    }

    if ($LASTEXITCODE -ne 0) {
        $global:LASTEXITCODE = 0
        return $null
    }

    return ($output -join [Environment]::NewLine)
}

function Remove-ScheduledTaskIfExists([string]$TaskName) {
    try {
        & schtasks.exe /Delete /TN $TaskName /F 2>$null | Out-Null
    }
    catch {
    }

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
    try {
        & schtasks.exe /Create /TN $TaskName /SC ONLOGON /TR $taskCommand /F 2>$null | Out-Null
    }
    catch {
        $global:LASTEXITCODE = 1
    }

    if ($LASTEXITCODE -ne 0) {
        $global:LASTEXITCODE = 0
        return $false
    }

    return $true
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $repoRoot "artifacts\desktop\win-x64\installer\EDetection-Setup-win-x64.exe"
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
$nonProductInstallLog = Join-Path $smokeRoot "non-product-install.log"
$falsePositiveProductInstallLog = Join-Path $smokeRoot "false-positive-product-install.log"
$uninstallLog = Join-Path $smokeRoot "uninstall.log"
$entryPoint = "EDetection.exe"
$installedExe = Join-Path $installFull $entryPoint
$appPathsKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$entryPoint"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$startupEntryName = "EDetection"
$startupTaskName = "EDetection Autostart"
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

    $nonProductFull = Join-Path $smokeRoot "existing-user-folder"
    $nonProductMarker = Join-Path $nonProductFull "important-user-file.txt"
    New-Item -ItemType Directory -Force -Path $nonProductFull | Out-Null
    Set-Content -Path $nonProductMarker -Value "must survive rejected install" -Encoding ASCII
    $nonProductProcess = Start-Process `
        -FilePath $installerFull `
        -ArgumentList @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/SP-",
            "/DIR=$nonProductFull",
            "/LOG=$nonProductInstallLog"
        ) `
        -Wait `
        -PassThru `
        -NoNewWindow
    if ($nonProductProcess.ExitCode -eq 0) {
        throw "Installer smoke failed: non-product install directory was accepted: $nonProductFull"
    }

    if (!(Test-Path $nonProductMarker)) {
        throw "Installer smoke failed: non-product directory marker was removed: $nonProductMarker"
    }

    if (Test-Path (Join-Path $nonProductFull $entryPoint)) {
        throw "Installer smoke failed: app was installed into rejected non-product directory: $nonProductFull"
    }

    $falsePositiveProductFull = Join-Path $smokeRoot "false-positive-product-folder"
    $falsePositiveProductAssets = Join-Path $falsePositiveProductFull "Assets"
    $falsePositiveProductMarker = Join-Path $falsePositiveProductAssets "important-user-file.txt"
    New-Item -ItemType Directory -Force -Path $falsePositiveProductAssets | Out-Null
    Set-Content -Path (Join-Path $falsePositiveProductFull "install-manifest.json") -Value '{"Product":"not E-Detection"}' -Encoding ASCII
    Set-Content -Path $falsePositiveProductMarker -Value "must survive rejected install" -Encoding ASCII
    $falsePositiveProductProcess = Start-Process `
        -FilePath $installerFull `
        -ArgumentList @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/SP-",
            "/DIR=$falsePositiveProductFull",
            "/LOG=$falsePositiveProductInstallLog"
        ) `
        -Wait `
        -PassThru `
        -NoNewWindow
    if ($falsePositiveProductProcess.ExitCode -eq 0) {
        throw "Installer smoke failed: false-positive product directory was accepted: $falsePositiveProductFull"
    }

    if (!(Test-Path $falsePositiveProductMarker)) {
        throw "Installer smoke failed: false-positive product directory marker was removed: $falsePositiveProductMarker"
    }

    if (Test-Path (Join-Path $falsePositiveProductFull $entryPoint)) {
        throw "Installer smoke failed: app was installed into rejected false-positive product directory: $falsePositiveProductFull"
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
    $legacyPythonExecutable = Join-Path $installFull "python-runtime\python.exe"
    $settingsBeforeUpdate = [ordered]@{
        SettingsVersion = 8
        InstallerUpdateSmokeMarker = $settingsMarker
    }

    # Simulate an upgrade from the retired implementation. The native
    # installer must remove the former runtime directory without touching
    # user settings or untracked user files.
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $legacyPythonExecutable) | Out-Null
    Set-Content -Path $legacyPythonExecutable -Value "legacy runtime" -Encoding ASCII

    $settingsBeforeUpdate | ConvertTo-Json | Set-Content -Path $settingsPath -Encoding UTF8

    $userOwnedTopLevelFile = Join-Path $installFull "user-owned-notes.txt"
    $staleManifestTopLevelFile = Join-Path $installFull "obsolete-product-root-file.txt"
    $installFilesManifest = Join-Path $installFull "install-files.txt"
    $staleRuntimeDirectory = Join-Path $installFull "python-runtime\obsolete-package"
    New-Item -ItemType Directory -Force -Path $staleRuntimeDirectory | Out-Null
    Set-Content -Path $userOwnedTopLevelFile -Value "user-owned file" -Encoding ASCII
    Set-Content -Path $staleManifestTopLevelFile -Value "stale product file" -Encoding ASCII
    Set-Content -Path (Join-Path $staleRuntimeDirectory "stale.txt") -Value "stale runtime file" -Encoding ASCII
    if (!(Test-Path $installFilesManifest)) {
        throw "Installer smoke failed: install files manifest was not installed at $installFilesManifest"
    }

    Add-Content -Path $installFilesManifest -Value "obsolete-product-root-file.txt" -Encoding UTF8

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

    & $healthScript -PackagePath $installFull -AllowUntrackedFiles
    if (!(Test-Path $userOwnedTopLevelFile)) {
        throw "Installer smoke failed: user-owned top-level file was removed during update: $userOwnedTopLevelFile"
    }

    if (Test-Path $staleManifestTopLevelFile) {
        throw "Installer smoke failed: stale manifest top-level file was not removed during update: $staleManifestTopLevelFile"
    }

    if (Test-Path $staleRuntimeDirectory) {
        throw "Installer smoke failed: stale runtime directory was not removed during update: $staleRuntimeDirectory"
    }

    $settingsAfterUpdate = Get-Content -Path $settingsPath -Raw | ConvertFrom-Json
    if ($settingsAfterUpdate.InstallerUpdateSmokeMarker -ne $settingsMarker) {
        throw "Installer smoke failed: user settings were not preserved during update/repair install."
    }

    if (Test-Path $legacyPythonExecutable) {
        throw "Installer smoke failed: native update did not remove retired runtime: $legacyPythonExecutable"
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
