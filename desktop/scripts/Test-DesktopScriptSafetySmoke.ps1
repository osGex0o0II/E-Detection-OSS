[CmdletBinding()]
param(
    [ValidateSet('All', 'Installer', 'Startup')]
    [string]$CaseName = 'All'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Resolve-Path (Join-Path $scriptDir '..\..')).Path)
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\desktop\script-safety-smoke'))
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\desktop'))
$expectedPrefix = $expectedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $artifactRoot.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a script safety directory outside '$expectedRoot': $artifactRoot"
}

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

if ($CaseName -in @('All', 'Installer')) {
    $fixtureRepo = Join-Path $artifactRoot 'fixture-repo'
    $fixtureScripts = Join-Path $fixtureRepo 'desktop\scripts'
    $fixtureInstaller = Join-Path $fixtureRepo 'desktop\installer'
    $fixtureProject = Join-Path $fixtureRepo 'desktop\EDetection.Desktop'
    $fixtureSource = Join-Path $artifactRoot 'source'
    $siblingOutput = Join-Path $fixtureRepo 'artifacts\desktop\win-x64-backup'
    New-Item -ItemType Directory -Path $fixtureScripts, $fixtureInstaller, $fixtureProject, $fixtureSource, $siblingOutput -Force | Out-Null

    Copy-Item -LiteralPath (Join-Path $scriptDir 'Build-DesktopInstaller.ps1') -Destination $fixtureScripts
    $pathSafetySource = Join-Path $scriptDir 'DesktopPathSafety.ps1'
    if (Test-Path -LiteralPath $pathSafetySource) {
        Copy-Item -LiteralPath $pathSafetySource -Destination $fixtureScripts
    }

    Set-Content -LiteralPath (Join-Path $fixtureInstaller 'E-Detection.Desktop.iss') -Value '; fixture' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $fixtureProject 'EDetection.Desktop.csproj') -Value '<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $fixtureSource 'EDetection.exe') -Value 'fixture' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $fixtureSource 'Test-DesktopPackageHealth.ps1') -Value 'param([string]$PackagePath)' -Encoding utf8NoBOM
    $sentinelPath = Join-Path $siblingOutput 'sentinel.txt'
    Set-Content -LiteralPath $sentinelPath -Value 'preserve' -Encoding utf8NoBOM

    $rejected = $false
    try {
        & (Join-Path $fixtureScripts 'Build-DesktopInstaller.ps1') `
            -RuntimeIdentifier win-x64 `
            -SourcePath $fixtureSource `
            -OutputDirectory $siblingOutput `
            -IsccPath (Join-Path $artifactRoot 'missing-iscc.exe')
    }
    catch {
        $rejected = $true
    }

    if (-not $rejected) {
        throw 'Installer output outside the runtime artifact root must be rejected.'
    }
    if (-not (Test-Path -LiteralPath $sentinelPath)) {
        throw 'Rejected sibling-prefix installer output must remain untouched.'
    }
    Write-Host 'PASS installer-sibling-prefix-is-preserved'
}

if ($CaseName -in @('All', 'Startup')) {
    $pathSafetyPath = Join-Path $scriptDir 'DesktopPathSafety.ps1'
    if (-not (Test-Path -LiteralPath $pathSafetyPath)) {
        throw 'DesktopPathSafety.ps1 must provide shared startup ownership checks.'
    }
    . $pathSafetyPath

    $installDirectory = Join-Path $artifactRoot 'App With Space'
    $installedExe = Join-Path $installDirectory 'EDetection.exe'
    $siblingExe = Join-Path "${installDirectory}Backup" 'EDetection.exe'
    if (-not (Test-RegisteredCommandTargetsInstall "`"$installedExe`" --background-startup" $installDirectory)) {
        throw 'A quoted startup command for the installed executable must match.'
    }
    if (-not (Test-RegisteredCommandTargetsInstall "$installedExe --background-startup" $installDirectory)) {
        throw 'An unquoted startup command for the installed executable must match.'
    }
    if (Test-RegisteredCommandTargetsInstall "`"$siblingExe`" --background-startup" $installDirectory) {
        throw 'A sibling-prefix executable must not match the install directory.'
    }
    if (Test-RegisteredCommandTargetsInstall "$siblingExe --background-startup" $installDirectory) {
        throw 'An unquoted sibling-prefix executable must not match the install directory.'
    }
    Write-Host 'PASS startup-command-ownership-is-exact'
}

Write-Host "Desktop script safety smoke passed: $CaseName"
