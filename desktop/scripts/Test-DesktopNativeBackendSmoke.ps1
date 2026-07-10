[CmdletBinding()]
param(
    [string]$ProjectPath = "",

    [string]$Configuration = "Debug",

    [switch]$NoBuild,

    [ValidateRange(10, 600)]
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $repoRoot "desktop\EDetection.Desktop.Tests\EDetection.Desktop.Tests.csproj"
}

$projectFull = [System.IO.Path]::GetFullPath((Resolve-Path $ProjectPath).Path)

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    throw "Native backend smoke failed: dotnet was not found on PATH."
}

$args = @(
    "run",
    "--project",
    $projectFull,
    "-c",
    $Configuration
)

if ($NoBuild) {
    $args += "--no-build"
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $dotnetCommand.Source
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
foreach ($argument in $args) {
    [void]$startInfo.ArgumentList.Add($argument)
}

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
if (!$process.Start()) {
    throw "Native backend smoke failed: unable to start dotnet run."
}

$standardOutputTask = $process.StandardOutput.ReadToEndAsync()
$standardErrorTask = $process.StandardError.ReadToEndAsync()
if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
    try {
        $process.Kill($true)
    }
    catch {
    }

    $process.WaitForExit()
    $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
    $standardError = $standardErrorTask.GetAwaiter().GetResult()
    throw "Native backend smoke timed out after $TimeoutSeconds seconds.$([Environment]::NewLine)stdout:$([Environment]::NewLine)$standardOutput$([Environment]::NewLine)stderr:$([Environment]::NewLine)$standardError"
}

$standardOutput = $standardOutputTask.GetAwaiter().GetResult()
$standardError = $standardErrorTask.GetAwaiter().GetResult()
if ($process.ExitCode -ne 0) {
    throw "Native backend smoke failed: dotnet run exited with $($process.ExitCode).$([Environment]::NewLine)stdout:$([Environment]::NewLine)$standardOutput$([Environment]::NewLine)stderr:$([Environment]::NewLine)$standardError"
}

if (![string]::IsNullOrWhiteSpace($standardOutput)) {
    Write-Host $standardOutput.TrimEnd()
}

if (![string]::IsNullOrWhiteSpace($standardError)) {
    Write-Warning $standardError.TrimEnd()
}

Write-Host "Native backend smoke passed: $projectFull"
