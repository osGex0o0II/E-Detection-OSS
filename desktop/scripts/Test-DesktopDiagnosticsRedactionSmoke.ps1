[CmdletBinding()]
param(
    [string]$ProjectPath = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $repoRoot "desktop\EDetection.Desktop"
}

$projectFull = [System.IO.Path]::GetFullPath((Resolve-Path $ProjectPath).Path)
$redactorPath = Join-Path $projectFull "Services\DiagnosticsRedactor.cs"
if (!(Test-Path $redactorPath)) {
    throw "Diagnostics redaction smoke failed: DiagnosticsRedactor.cs was not found at $redactorPath"
}

$source = Get-Content -Path $redactorPath -Raw
$checks = @(
    'UrlCredentialPattern',
    '%USERPROFILE%',
    '%LOCALAPPDATA%',
    '%APPDATA%',
    '%TEMP%',
    '{backendRoot}',
    '{python}',
    '{localPath}'
)

foreach ($check in $checks) {
    if ($source.IndexOf($check, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Diagnostics redaction smoke failed: expected redaction token '$check' was not found."
    }
}

$diagnosticsViewModelPath = Join-Path $projectFull "ViewModels\DiagnosticsViewModel.cs"
$diagnosticsViewModel = Get-Content -Path $diagnosticsViewModelPath -Raw
if ($diagnosticsViewModel.IndexOf("诊断信息: 已脱敏本机路径和凭据", [System.StringComparison]::Ordinal) -lt 0 `
    -or $diagnosticsViewModel.IndexOf("DiagnosticsRedactor.Redact", [System.StringComparison]::Ordinal) -lt 0) {
    throw "Diagnostics redaction smoke failed: diagnostics clipboard text is not explicitly redacted."
}

$runtimeLogServicePath = Join-Path $projectFull "Services\RuntimeLogService.cs"
$runtimeLogService = Get-Content -Path $runtimeLogServicePath -Raw
if ($runtimeLogService.IndexOf("DiagnosticsRedactor.Redact(item.Message)", [System.StringComparison]::Ordinal) -lt 0 `
    -or $runtimeLogService.IndexOf("说明\t诊断信息\t已脱敏本机路径和凭据", [System.StringComparison]::Ordinal) -lt 0) {
    throw "Diagnostics redaction smoke failed: runtime log export is not explicitly redacted."
}

Write-Host "Diagnostics redaction smoke passed: $projectFull"
