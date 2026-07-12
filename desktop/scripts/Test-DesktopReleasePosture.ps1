[CmdletBinding()]
param(
    [string]$WorkflowPath = "",
    [string]$PublishScriptPath = "",
    [string]$BuildInstallerScriptPath = "",
    [string]$InstallerSmokeScriptPath = "",
    [string]$NativeBackendSmokeScriptPath = "",
    [string]$PackageHealthFixtureScriptPath = "",
    [string]$PathSafetyScriptPath = "",
    [string]$ScriptSafetySmokeScriptPath = "",
    [string]$InstallScriptPath = "",
    [string]$UninstallScriptPath = "",
    [string]$DesktopProjectPath = "",
    [string]$RootReadmePath = "",
    [string]$DesktopReadmePath = "",
    [string]$MigrationDocumentPath = "",
    [string]$MainViewModelPath = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")

if ([string]::IsNullOrWhiteSpace($WorkflowPath)) { $WorkflowPath = Join-Path $repoRoot ".github\workflows\desktop.yml" }
if ([string]::IsNullOrWhiteSpace($PublishScriptPath)) { $PublishScriptPath = Join-Path $scriptDir "Publish-Desktop.ps1" }
if ([string]::IsNullOrWhiteSpace($BuildInstallerScriptPath)) { $BuildInstallerScriptPath = Join-Path $scriptDir "Build-DesktopInstaller.ps1" }
if ([string]::IsNullOrWhiteSpace($InstallerSmokeScriptPath)) { $InstallerSmokeScriptPath = Join-Path $scriptDir "Test-DesktopInstallerSmoke.ps1" }
if ([string]::IsNullOrWhiteSpace($NativeBackendSmokeScriptPath)) { $NativeBackendSmokeScriptPath = Join-Path $scriptDir "Test-DesktopNativeBackendSmoke.ps1" }
if ([string]::IsNullOrWhiteSpace($PackageHealthFixtureScriptPath)) { $PackageHealthFixtureScriptPath = Join-Path $scriptDir "Test-DesktopPackageHealthFixture.ps1" }
if ([string]::IsNullOrWhiteSpace($PathSafetyScriptPath)) { $PathSafetyScriptPath = Join-Path $scriptDir "DesktopPathSafety.ps1" }
if ([string]::IsNullOrWhiteSpace($ScriptSafetySmokeScriptPath)) { $ScriptSafetySmokeScriptPath = Join-Path $scriptDir "Test-DesktopScriptSafetySmoke.ps1" }
if ([string]::IsNullOrWhiteSpace($InstallScriptPath)) { $InstallScriptPath = Join-Path $scriptDir "Install-Desktop.ps1" }
if ([string]::IsNullOrWhiteSpace($UninstallScriptPath)) { $UninstallScriptPath = Join-Path $scriptDir "Uninstall-Desktop.ps1" }
if ([string]::IsNullOrWhiteSpace($DesktopProjectPath)) { $DesktopProjectPath = Join-Path $repoRoot "desktop\EDetection.Desktop\EDetection.Desktop.csproj" }
if ([string]::IsNullOrWhiteSpace($RootReadmePath)) { $RootReadmePath = Join-Path $repoRoot "README.md" }
if ([string]::IsNullOrWhiteSpace($DesktopReadmePath)) { $DesktopReadmePath = Join-Path $repoRoot "desktop\README.md" }
if ([string]::IsNullOrWhiteSpace($MigrationDocumentPath)) { $MigrationDocumentPath = Join-Path $repoRoot "desktop\NATIVE_BACKEND_MIGRATION.md" }
if ([string]::IsNullOrWhiteSpace($MainViewModelPath)) { $MainViewModelPath = Join-Path $repoRoot "desktop\EDetection.Desktop\ViewModels\MainViewModel.cs" }

function Get-Text([string]$Path) {
    return Get-Content -LiteralPath ([System.IO.Path]::GetFullPath((Resolve-Path $Path).Path)) -Raw
}

function Assert-Contains([string]$Text, [string]$Expected, [string]$Message) {
    if ($Text.IndexOf($Expected, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Release posture check failed: $Message"
    }
}

function Assert-NotContains([string]$Text, [string]$Unexpected, [string]$Message) {
    if ($Text.IndexOf($Unexpected, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Release posture check failed: $Message"
    }
}

$workflow = Get-Text $WorkflowPath
$publish = Get-Text $PublishScriptPath
$buildInstaller = Get-Text $BuildInstallerScriptPath
$installerSmoke = Get-Text $InstallerSmokeScriptPath
$nativeBackendSmoke = Get-Text $NativeBackendSmokeScriptPath
$packageHealthFixture = Get-Text $PackageHealthFixtureScriptPath
$pathSafety = Get-Text $PathSafetyScriptPath
$scriptSafetySmoke = Get-Text $ScriptSafetySmokeScriptPath
$install = Get-Text $InstallScriptPath
$uninstall = Get-Text $UninstallScriptPath
$desktopProject = Get-Text $DesktopProjectPath
$rootReadme = Get-Text $RootReadmePath
$desktopReadme = Get-Text $DesktopReadmePath
$migrationDocument = Get-Text $MigrationDocumentPath
$mainViewModel = Get-Text $MainViewModelPath

Assert-Contains $workflow "run: ./desktop/scripts/Publish-Desktop.ps1 -RuntimeIdentifier win-x64" "workflow must publish the native package."
Assert-Contains $workflow "run: ./artifacts/desktop/win-x64/publish/Test-DesktopPackageHealth.ps1 -PackagePath ./artifacts/desktop/win-x64/publish" "workflow must validate the published package."
Assert-Contains $workflow "run: ./artifacts/desktop/win-x64/publish/Test-DesktopInstallSmoke.ps1 -PackagePath ./artifacts/desktop/win-x64/publish" "workflow must validate portable installation."
Assert-Contains $workflow "run: ./desktop/scripts/Build-DesktopInstaller.ps1 -RuntimeIdentifier win-x64" "workflow must build the native installer."
Assert-Contains $workflow "run: ./desktop/scripts/Test-DesktopInstallerSmoke.ps1 -InstallerPath ./artifacts/desktop/win-x64/installer/EDetection-Setup-win-x64.exe" "workflow must validate installer upgrade behavior."
Assert-Contains $workflow 'if ($portableBackend -ne "native")' "release job must reject a non-native portable package."
Assert-Contains $workflow '& $portableHealthScript -PackagePath $portablePackageRoot' "release job must re-run package health after download."
Assert-Contains $workflow 'needs: winui-build' "release job must depend on the native build."

foreach ($removedWorkflowToken in @("actions/setup-python", "pip install", "pyproject.toml", "requirements-runtime.lock", "PackageProfile")) {
    Assert-NotContains $workflow $removedWorkflowToken "workflow must not retain removed implementation dependency '$removedWorkflowToken'."
}

Assert-Contains $publish '"DetectionBackend=native"' "release-info must identify the native backend."
Assert-Contains $publish '"PackageContents=Native C# + .NET runtime only"' "release-info must describe native-only contents."
Assert-Contains $publish '$zipPath = Join-Path $artifactRoot "EDetection-$RuntimeIdentifier.zip"' "publish must use the stable native archive name."
Assert-Contains $publish '$installerName = "EDetection-Setup-$RuntimeIdentifier.exe"' "publish must use the stable native installer name."
Assert-Contains $publish '"DesktopPathSafety.ps1"' "publish must include the shared path-safety helper."
Assert-Contains $publish '"Test-DesktopScriptSafetySmoke.ps1"' "publish must include the script-safety smoke."
foreach ($removedPublishToken in @("PackageProfile", "python-runtime", "python-wheelhouse", "BundledPython", "PythonPath", "SkipPythonWheelhouse")) {
    Assert-NotContains $publish $removedPublishToken "publish must not retain '$removedPublishToken'."
}

foreach ($scriptText in @($buildInstaller, $installerSmoke)) {
    Assert-NotContains $scriptText "PackageProfile" "installer build and smoke scripts must have no profile switch."
    Assert-NotContains $scriptText "InstallerProfileSuffix" "installer build and smoke scripts must have no profile suffix."
}
Assert-Contains $installerSmoke '"python-runtime\python.exe"' "installer smoke must cover cleanup of a legacy runtime on upgrade."
Assert-Contains $nativeBackendSmoke 'desktop\EDetection.Desktop.Tests\EDetection.Desktop.Tests.csproj' "native backend smoke must run the native test project."
Assert-NotContains $nativeBackendSmoke 'Get-Command python' "native backend smoke must not require Python."
Assert-Contains $packageHealthFixture 'DetectionBackend=native' "package-health fixture must cover native release metadata."
Assert-Contains $pathSafety 'function Test-PathInsideDirectory' "shared path safety must enforce canonical containment."
Assert-Contains $pathSafety 'function Get-RegisteredExecutablePath' "shared path safety must parse registered commands."
Assert-Contains $pathSafety 'function Test-RegisteredCommandTargetsInstall' "shared path safety must compare startup executables exactly."
Assert-Contains $scriptSafetySmoke 'win-x64-backup' "script safety smoke must cover sibling-prefix output paths."
Assert-Contains $scriptSafetySmoke 'App With Space' "script safety smoke must cover quoted and unquoted commands."
Assert-NotContains $buildInstaller '$outputFull.StartsWith($artifactRootFull' "installer output safety must not use a raw prefix check."
Assert-NotContains $publish '$publishDirFull.StartsWith($artifactRootFull' "publish cleanup safety must not use a raw prefix check."
Assert-Contains $install '. (Join-Path $scriptDir "DesktopPathSafety.ps1")' "portable install must load shared path safety."
Assert-Contains $uninstall '. (Join-Path $scriptDir "DesktopPathSafety.ps1")' "portable uninstall must load shared path safety."
Assert-Contains $uninstall 'Test-RegisteredCommandTargetsInstall $startupValue $installFull $entryPoint' "startup registry cleanup must compare the executable exactly."
Assert-Contains $uninstall "SelectSingleNode(`"//*[local-name()='Exec']/*[local-name()='Command']`")" "scheduled-task cleanup must parse the executable command from XML."
Assert-NotContains $uninstall '$startupValue.IndexOf($installFull' "startup cleanup must not use substring path matching."
Assert-NotContains $uninstall '$xml.IndexOf($installFull' "scheduled-task cleanup must not search raw XML for the install path."

Assert-Contains $desktopProject '<Version>2.0.2</Version>' "desktop package version must be 2.0.2."
Assert-Contains $desktopProject '<AssemblyVersion>2.0.2.0</AssemblyVersion>' "assembly version must be 2.0.2.0."
Assert-Contains $desktopProject '<FileVersion>2.0.2.0</FileVersion>' "file version must be 2.0.2.0."
foreach ($releaseText in @($workflow, $publish)) {
    Assert-Contains $releaseText 'intentionally unsigned' "release workflow and package text must explicitly identify unsigned assets."
    Assert-Contains $releaseText 'SHA-256 verifies file integrity, not publisher identity' "release workflow and package text must distinguish integrity from publisher identity."
}
foreach ($documentation in @($rootReadme, $desktopReadme, $migrationDocument)) {
    Assert-Contains $documentation '未签名' "release documentation must say that 2.0.2 is unsigned."
    Assert-Contains $documentation 'SHA-256 仅验证文件完整性，不能验证发布者身份' "release documentation must explain the checksum trust boundary."
}
Assert-NotContains $rootReadme '正式 GitHub Release 必须通过 Authenticode 签名门禁' "root README must not claim that this release requires signing."
Assert-NotContains $desktopReadme '正式发布必须满足 GitHub 工作流的签名' "desktop README must not claim that this release requires signing."
Assert-NotContains $migrationDocument '正式 GitHub Release 仍须通过 Authenticode 签名' "migration notes must not claim that this release requires signing."
Assert-Contains $mainViewModel 'SHA-256 仅验证文件完整性，不能验证发布者身份' "updater success copy must state the checksum trust boundary."
Assert-Contains $mainViewModel '2.0.2 安装向导未签名' "updater success copy must identify the unsigned installer."
Assert-Contains $workflow '$forceUnsigned = $tag -eq "v2.0.2"' "the v2.0.2 workflow must force unsigned release assets even if signing secrets exist."

[pscustomobject]@{
    WorkflowPath = [System.IO.Path]::GetFullPath((Resolve-Path $WorkflowPath).Path)
    PublishScriptPath = [System.IO.Path]::GetFullPath((Resolve-Path $PublishScriptPath).Path)
    CheckedAt = (Get-Date).ToString("o")
    Passed = $true
} | ConvertTo-Json
