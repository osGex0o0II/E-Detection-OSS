[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path,

    [switch]$RequireSigned,

    [switch]$AsJson
)

$ErrorActionPreference = "Stop"

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

$targetFiles = @(Get-TargetFiles $Path)
if ($targetFiles.Count -eq 0) {
    throw "No files were found for signature inspection."
}

$results = foreach ($file in $targetFiles) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    [pscustomobject]@{
        Path = $file.FullName
        Status = $signature.Status.ToString()
        SignerSubject = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { "" }
        SignerThumbprint = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { "" }
        TimeStamperSubject = if ($null -ne $signature.TimeStamperCertificate) { $signature.TimeStamperCertificate.Subject } else { "" }
    }
}

if ($AsJson) {
    $results | ConvertTo-Json -Depth 4
}
else {
    $results | Format-Table -AutoSize
}

if ($RequireSigned) {
    $unsigned = @($results | Where-Object { $_.Status -ne "Valid" })
    if ($unsigned.Count -gt 0) {
        throw "Signature validation failed: $($unsigned.Count) file(s) are not validly signed."
    }
}
