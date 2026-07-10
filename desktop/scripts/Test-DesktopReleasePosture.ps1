[CmdletBinding()]
param(
    [string]$WorkflowPath = "",
    [string]$PublishScriptPath = "",
    [string]$BuildInstallerScriptPath = "",
    [string]$InstallerSmokeScriptPath = "",
    [string]$NativeBackendSmokeScriptPath = "",
    [string]$PackageHealthFixtureScriptPath = ""
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

Assert-Contains $workflow "run: ./desktop/scripts/Publish-Desktop.ps1 -RuntimeIdentifier win-x64" "workflow must publish the native package."
Assert-Contains $workflow "run: ./artifacts/desktop/win-x64/publish/Test-DesktopPackageHealth.ps1 -PackagePath ./artifacts/desktop/win-x64/publish" "workflow must validate the published package."
Assert-Contains $workflow "run: ./artifacts/desktop/win-x64/publish/Test-DesktopInstallSmoke.ps1 -PackagePath ./artifacts/desktop/win-x64/publish" "workflow must validate portable installation."
Assert-Contains $workflow "run: ./desktop/scripts/Build-DesktopInstaller.ps1 -RuntimeIdentifier win-x64" "workflow must build the native installer."
Assert-Contains $workflow "run: ./desktop/scripts/Test-DesktopInstallerSmoke.ps1 -InstallerPath ./artifacts/desktop/win-x64/installer/E-Detection.Desktop-Setup-win-x64.exe" "workflow must validate installer upgrade behavior."
Assert-Contains $workflow 'if ($portableBackend -ne "native")' "release job must reject a non-native portable package."
Assert-Contains $workflow '& $portableHealthScript -PackagePath $portablePackageRoot' "release job must re-run package health after download."
Assert-Contains $workflow 'needs: winui-build' "release job must depend on the native build."

foreach ($removedWorkflowToken in @("actions/setup-python", "pip install", "pyproject.toml", "requirements-runtime.lock", "PackageProfile")) {
    Assert-NotContains $workflow $removedWorkflowToken "workflow must not retain removed implementation dependency '$removedWorkflowToken'."
}

Assert-Contains $publish '"DetectionBackend=native"' "release-info must identify the native backend."
Assert-Contains $publish '"PackageContents=Native C# + .NET runtime only"' "release-info must describe native-only contents."
Assert-Contains $publish '$zipPath = Join-Path $artifactRoot "E-Detection.Desktop-$RuntimeIdentifier.zip"' "publish must use the stable native archive name."
Assert-Contains $publish '$installerName = "E-Detection.Desktop-Setup-$RuntimeIdentifier.exe"' "publish must use the stable native installer name."
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

[pscustomobject]@{
    WorkflowPath = [System.IO.Path]::GetFullPath((Resolve-Path $WorkflowPath).Path)
    PublishScriptPath = [System.IO.Path]::GetFullPath((Resolve-Path $PublishScriptPath).Path)
    CheckedAt = (Get-Date).ToString("o")
    Passed = $true
} | ConvertTo-Json
