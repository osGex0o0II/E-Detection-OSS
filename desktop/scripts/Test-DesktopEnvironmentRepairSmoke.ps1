[CmdletBinding()]
param(
    [string]$PackagePath = "",

    [string]$BackendRoot = "",

    [string]$PythonExecutable = "",

    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-BackendRoot([string]$StartDirectory) {
    $current = Get-Item -LiteralPath $StartDirectory
    while ($current -ne $null) {
        $hasPyproject = Test-Path (Join-Path $current.FullName "pyproject.toml")
        $hasPackageSource = Test-Path (Join-Path $current.FullName "e_detection")
        if ($hasPyproject -and $hasPackageSource) {
            return $current.FullName
        }

        $current = $current.Parent
    }

    return ""
}

if ([string]::IsNullOrWhiteSpace($BackendRoot)) {
    $BackendRoot = Find-BackendRoot $scriptDir
}

if ([string]::IsNullOrWhiteSpace($BackendRoot) -and ![string]::IsNullOrWhiteSpace($PackagePath)) {
    $packageFull = [System.IO.Path]::GetFullPath((Resolve-Path $PackagePath).Path)
    $BackendRoot = Find-BackendRoot $packageFull
}

if ([string]::IsNullOrWhiteSpace($BackendRoot)) {
    $repoCandidate = Join-Path $scriptDir "..\.."
    if (Test-Path $repoCandidate) {
        $BackendRoot = Find-BackendRoot ([System.IO.Path]::GetFullPath($repoCandidate))
    }
}

if ([string]::IsNullOrWhiteSpace($BackendRoot)) {
    throw "Backend source was not found. Pass -BackendRoot to the repository root."
}

$repoRoot = Resolve-Path $BackendRoot

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = if (![string]::IsNullOrWhiteSpace($PackagePath)) {
        Join-Path ([System.IO.Path]::GetFullPath((Resolve-Path $PackagePath).Path)) "smoke-results\environment-repair"
    }
    else {
        Join-Path $repoRoot "artifacts\desktop\environment-repair-smoke"
    }
}

if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $PythonExecutable = "python"
}

$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputFull | Out-Null

$backendRoot = [System.IO.Path]::GetFullPath($repoRoot)
$pyprojectPath = Join-Path $backendRoot "pyproject.toml"
$packageSourcePath = Join-Path $backendRoot "e_detection"
if (!(Test-Path $pyprojectPath) -or !(Test-Path $packageSourcePath)) {
    throw "Backend source was not found at $backendRoot"
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$venvRoot = Join-Path $outputFull "venv-$timestamp"
$repairLogPath = Join-Path $outputFull "repair-$timestamp.log"
$resultPath = Join-Path $outputFull "environment-repair-$timestamp.json"

function Invoke-Python([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $process.Start() | Out-Null
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    if (!$process.WaitForExit(300000)) {
        try {
            $process.Kill($true)
        }
        catch {
        }

        throw "Timed out running $Executable $($Arguments -join ' ')"
    }

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout.Trim()
        StdErr = $stderr.Trim()
    }
}

$createVenv = Invoke-Python $PythonExecutable @("-m", "venv", $venvRoot) $backendRoot
if ($createVenv.ExitCode -ne 0) {
    throw "Failed to create temporary venv: $($createVenv.StdErr)"
}

$venvPython = Join-Path $venvRoot "Scripts\python.exe"
if (!(Test-Path $venvPython)) {
    throw "Temporary venv Python was not created at $venvPython"
}

$probeScript = "import importlib.util, sys; spec = importlib.util.find_spec('e_detection'); print('found=' + str(spec is not None)); sys.exit(20) if spec is None else None; import e_detection.cli"
$beforeProbe = Invoke-Python $venvPython @("-c", $probeScript) $backendRoot
if ($beforeProbe.ExitCode -eq 0) {
    throw "Temporary venv unexpectedly imports e_detection before repair."
}

$repair = Invoke-Python $venvPython @("-m", "pip", "install", "-e", $backendRoot) $backendRoot
@(
    "ExitCode: $($repair.ExitCode)"
    "STDOUT:"
    $repair.StdOut
    "STDERR:"
    $repair.StdErr
) | Set-Content -Path $repairLogPath -Encoding UTF8

if ($repair.ExitCode -ne 0) {
    throw "Environment repair command failed. See $repairLogPath"
}

$afterScript = "import e_detection, sys; print('module=' + (getattr(e_detection, '__file__', '') or 'unknown')); print('version=' + getattr(e_detection, '__version__', 'unknown'))"
$afterProbe = Invoke-Python $venvPython @("-c", $afterScript) $backendRoot
if ($afterProbe.ExitCode -ne 0) {
    throw "e_detection is still not importable after repair: $($afterProbe.StdErr)"
}

$moduleLine = ($afterProbe.StdOut -split [Environment]::NewLine | Where-Object { $_ -like "module=*" } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($moduleLine) -or $moduleLine -notlike "*e_detection*") {
    throw "Probe did not report the repaired e_detection module path."
}

$result = [pscustomobject]@{
    Passed = $true
    BackendRoot = $backendRoot
    VenvPython = $venvPython
    BeforeExitCode = $beforeProbe.ExitCode
    RepairExitCode = $repair.ExitCode
    Module = $moduleLine.Replace("module=", "")
    RepairLogPath = $repairLogPath
    CheckedAt = (Get-Date).ToString("o")
}

$result | ConvertTo-Json | Set-Content -Path $resultPath -Encoding UTF8
Write-Host "Desktop environment repair smoke passed: $resultPath"
