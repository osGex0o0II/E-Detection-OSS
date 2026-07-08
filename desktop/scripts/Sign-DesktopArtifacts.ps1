[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path,

    [string]$CertificateBase64 = $env:WINDOWS_CODE_SIGNING_PFX_BASE64,

    [string]$CertificatePassword = $env:WINDOWS_CODE_SIGNING_PFX_PASSWORD,

    [string]$TimestampUrl = "",

    [string]$SignToolPath = "",

    [switch]$Optional
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $TimestampUrl = if ([string]::IsNullOrWhiteSpace($env:WINDOWS_CODE_SIGNING_TIMESTAMP_URL)) {
        "http://timestamp.digicert.com"
    }
    else {
        $env:WINDOWS_CODE_SIGNING_TIMESTAMP_URL
    }
}

function Resolve-SignToolPath {
    param([string]$RequestedPath)

    if (![string]::IsNullOrWhiteSpace($RequestedPath)) {
        return $RequestedPath
    }

    if (![string]::IsNullOrWhiteSpace($env:SIGNTOOL_PATH)) {
        return $env:SIGNTOOL_PATH
    }

    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Recurse -File -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    throw "signtool.exe was not found. Install the Windows SDK or pass -SignToolPath."
}

function Get-TargetFiles {
    param([string[]]$InputPaths)

    $files = @()
    foreach ($item in $InputPaths) {
        if ([string]::IsNullOrWhiteSpace($item)) {
            continue
        }

        $resolved = Resolve-Path -LiteralPath $item -ErrorAction Stop
        foreach ($pathInfo in $resolved) {
            $fullPath = [System.IO.Path]::GetFullPath($pathInfo.Path)
            if (Test-Path -LiteralPath $fullPath -PathType Container) {
                $files += Get-ChildItem -LiteralPath $fullPath -Recurse -File -Include "*.exe", "*.dll", "*.msix", "*.msixbundle", "*.appx", "*.appxbundle"
            }
            else {
                $files += Get-Item -LiteralPath $fullPath
            }
        }
    }

    $files | Sort-Object FullName -Unique
}

if ([string]::IsNullOrWhiteSpace($CertificateBase64)) {
    if ($Optional) {
        Write-Host "Code signing skipped: WINDOWS_CODE_SIGNING_PFX_BASE64 is not configured."
        exit 0
    }

    throw "WINDOWS_CODE_SIGNING_PFX_BASE64 is required for code signing."
}

if ([string]::IsNullOrWhiteSpace($CertificatePassword)) {
    if ($Optional) {
        Write-Host "Code signing skipped: WINDOWS_CODE_SIGNING_PFX_PASSWORD is not configured."
        exit 0
    }

    throw "WINDOWS_CODE_SIGNING_PFX_PASSWORD is required for code signing."
}

$targetFiles = @(Get-TargetFiles $Path)
if ($targetFiles.Count -eq 0) {
    throw "No signable files were found."
}

$signTool = Resolve-SignToolPath $SignToolPath
if (!(Test-Path -LiteralPath $signTool)) {
    throw "signtool.exe was not found at $signTool"
}

$pfxPath = Join-Path $env:TEMP ("E-Detection-CodeSigning-{0}.pfx" -f ([Guid]::NewGuid().ToString("N")))
try {
    [System.IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($CertificateBase64))

    foreach ($file in $targetFiles) {
        Write-Host "Signing $($file.FullName)"
        & $signTool sign /f $pfxPath /p $CertificatePassword /fd SHA256 /td SHA256 /tr $TimestampUrl $file.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "signtool sign failed for $($file.FullName) with exit code $LASTEXITCODE."
        }

        & $signTool verify /pa /tw $file.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "signtool verify failed for $($file.FullName) with exit code $LASTEXITCODE."
        }
    }
}
finally {
    Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
}

Write-Host "Code signing completed for $($targetFiles.Count) file(s)."
