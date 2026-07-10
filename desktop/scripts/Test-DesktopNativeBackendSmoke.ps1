[CmdletBinding()]
param(
    [string]$ProjectPath = "",

    [string]$Configuration = "Debug",

    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $repoRoot "desktop\EDetection.Desktop.Tests\EDetection.Desktop.Tests.csproj"
}

$projectFull = [System.IO.Path]::GetFullPath((Resolve-Path $ProjectPath).Path)

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
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

& dotnet @args
if ($LASTEXITCODE -ne 0) {
    throw "Native backend smoke failed: dotnet run exited with $LASTEXITCODE."
}

Write-Host "Native backend smoke passed: $projectFull"
