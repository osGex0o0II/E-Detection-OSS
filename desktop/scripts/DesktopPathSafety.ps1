function Test-PathInsideDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CandidatePath,

        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    $candidateFull = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetFullPath($CandidatePath))
    $rootFull = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetFullPath($RootPath))
    $rootWithSeparator = if ($rootFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $rootFull
    }
    else {
        $rootFull + [System.IO.Path]::DirectorySeparatorChar
    }

    return [string]::Equals(
        $candidateFull,
        $rootFull,
        [System.StringComparison]::OrdinalIgnoreCase) `
        -or $candidateFull.StartsWith(
            $rootWithSeparator,
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-RegisteredExecutablePath {
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Command
    )

    if ([string]::IsNullOrWhiteSpace($Command)) {
        return $null
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Command).Trim()
    $quoted = [regex]::Match($expanded, '^"(?<path>[^"]+)"(?:\s|$)')
    $candidate = if ($quoted.Success) {
        $quoted.Groups['path'].Value
    }
    else {
        $unquoted = [regex]::Match(
            $expanded,
            '^(?<path>.+?\.exe)(?=\s|$)',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $unquoted.Success) {
            return $null
        }

        $unquoted.Groups['path'].Value.Trim()
    }

    try {
        return [System.IO.Path]::GetFullPath($candidate)
    }
    catch {
        return $null
    }
}

function Test-RegisteredCommandTargetsInstall {
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [string]$InstallDirectory,

        [string]$EntryPoint = 'EDetection.exe'
    )

    $registeredExecutable = Get-RegisteredExecutablePath $Command
    if ([string]::IsNullOrWhiteSpace($registeredExecutable)) {
        return $false
    }

    $installedExecutable = [System.IO.Path]::GetFullPath(
        (Join-Path $InstallDirectory $EntryPoint))
    return [string]::Equals(
        $registeredExecutable,
        $installedExecutable,
        [System.StringComparison]::OrdinalIgnoreCase)
}
