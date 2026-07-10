[CmdletBinding()]
param(
    [string]$InstallerScriptPath = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($InstallerScriptPath)) {
    $InstallerScriptPath = Join-Path $scriptDir "..\installer\E-Detection.Desktop.iss"
}

$installerScriptFull = [System.IO.Path]::GetFullPath((Resolve-Path $InstallerScriptPath).Path)
$content = Get-Content -LiteralPath $installerScriptFull -Raw

function Assert-ContainsLiteral([string]$Text, [string]$Expected, [string]$Message) {
    if ($Text.IndexOf($Expected, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Installer static check failed: $Message"
    }
}

function Assert-MatchesRegex([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -notmatch $Pattern) {
        throw "Installer static check failed: $Message"
    }
}

$requiredLiterals = @(
    @{ Text = "#ifndef UnsafeInstallRootOverride"; Message = "UnsafeInstallRootOverride must be overrideable by installer smoke." },
    @{ Text = "#define UnsafeInstallRootOverride"; Message = "UnsafeInstallRootOverride preprocessor variable is required for installer smoke safety checks." },
    @{ Text = '#define UnsafeInstallRootOverride ""'; Message = "UnsafeInstallRootOverride default must be empty for production installers." },
    @{ Text = 'DefaultDirName={localappdata}\Programs\E-Detection Desktop'; Message = "Installer must default to the per-user LocalAppData app directory." },
    @{ Text = "PrivilegesRequired=lowest"; Message = "Installer must remain per-user and avoid elevation by default." },
    @{ Text = "OutputBaseFilename=E-Detection.Desktop-Setup-{#RuntimeIdentifier}"; Message = "Output filename must use the stable native runtime name." },
    @{ Text = "[InstallDelete]"; Message = "InstallDelete section is required to clean stale product directories." },
    @{ Text = 'Type: filesandordirs; Name: "{app}\core"; Check: ShouldCleanExistingProductDirectory'; Message = "InstallDelete must remove the retired core directory on upgrade." },
    @{ Text = 'Type: filesandordirs; Name: "{app}\e_detection"; Check: ShouldCleanExistingProductDirectory'; Message = "InstallDelete must remove the retired implementation directory on upgrade." },
    @{ Text = 'Type: filesandordirs; Name: "{app}\python-runtime"; Check: ShouldCleanExistingProductDirectory'; Message = "InstallDelete must remove the retired runtime directory on upgrade." },
    @{ Text = 'Type: filesandordirs; Name: "{app}\python-wheelhouse"; Check: ShouldCleanExistingProductDirectory'; Message = "InstallDelete must remove the retired wheelhouse directory on upgrade." },
    @{ Text = "[UninstallRun]"; Message = "UninstallRun section is required for desktop cleanup." },
    @{ Text = 'Uninstall-Desktop.ps1"" -InstallDirectory ""{app}"" -CleanupOnly -Quiet'; Message = "UninstallRun must invoke cleanup-only desktop uninstall script." },
    @{ Text = 'Flags: runhidden waituntilterminated'; Message = "UninstallRun must wait for cleanup to finish." },
    @{ Text = 'RunOnceId: "CleanupUserStartupEntries"'; Message = "UninstallRun must be idempotent." },
    @{ Text = "function IsUnsafeInstallDirectory"; Message = "Unsafe install directory guard is required." },
    @{ Text = "function LooksLikeExistingProductDirectory"; Message = "Installer must distinguish product directories from user folders." },
    @{ Text = "function IsInstallDirectoryAllowed"; Message = "Non-product install directory guard is required." },
    @{ Text = "function NextButtonClick"; Message = "Interactive installer must block unsafe directories before continuing." },
    @{ Text = "function PrepareToInstall"; Message = "Silent installer must block unsafe directories before install." },
    @{ Text = "procedure DeletePreviousInstallManifestFiles"; Message = "Manifest-based update cleanup is required." },
    @{ Text = "LoadStringsFromFile(AddBackslash(ExpandConstant('{app}')) + 'install-files.txt', Files)"; Message = "Update cleanup must read the installed package manifest." },
    @{ Text = "function IsSafeInstallManifestPath"; Message = "Manifest cleanup must validate relative paths." },
    @{ Text = "function ShouldPreserveInstallMarker"; Message = "Manifest cleanup must preserve product markers and uninstaller files." },
    @{ Text = "procedure CurStepChanged"; Message = "Installer must hook install-step cleanup." },
    @{ Text = "DeletePreviousInstallManifestFiles;"; Message = "Install step must run manifest-based cleanup." }
)

foreach ($required in $requiredLiterals) {
    Assert-ContainsLiteral $content $required.Text $required.Message
}

Assert-MatchesRegex `
    $content `
    "function\s+NextButtonClick[\s\S]*?IsUnsafeInstallDirectory\(WizardDirValue\)[\s\S]*?IsInstallDirectoryAllowed\(WizardDirValue\)" `
    "NextButtonClick must check unsafe directories and non-product directories."

Assert-MatchesRegex `
    $content `
    "function\s+PrepareToInstall[\s\S]*?IsUnsafeInstallDirectory\(WizardDirValue\)[\s\S]*?IsInstallDirectoryAllowed\(WizardDirValue\)" `
    "PrepareToInstall must check unsafe directories and non-product directories for silent installs."

Assert-MatchesRegex `
    $content `
    "function\s+IsUnsafeInstallDirectory[\s\S]*?UnsafeInstallRootOverride[\s\S]*?autopf[\s\S]*?commonpf[\s\S]*?commonpf32" `
    "Unsafe install directory guard must include smoke override and Program Files locations."

Assert-MatchesRegex `
    $content `
    "function\s+IsUnsafeInstallDirectory[\s\S]*?DriveRoot[\s\S]*?USERPROFILE[\s\S]*?userdesktop[\s\S]*?UnsafeInstallRootOverride[\s\S]*?autopf[\s\S]*?commonpf[\s\S]*?commonpf32" `
    "Unsafe install directory guard must reject drive roots, user roots, desktop, smoke override, and Program Files locations."

Assert-MatchesRegex `
    $content `
    "function\s+LooksLikeExistingProductDirectory[\s\S]*?FileExists\(AddBackslash\(Dir\) \+ '\{#AppExeName\}'\)[\s\S]*?release-info\.txt[\s\S]*?unins000\.dat[\s\S]*?EDetection\.Desktop\.dll" `
    "Product directory detection must require the app executable and product marker files."

Assert-MatchesRegex `
    $content `
    "function\s+IsInstallDirectoryAllowed[\s\S]*?not DirExists\(Dir\)[\s\S]*?IsDirectoryEmpty\(Dir\)[\s\S]*?LooksLikeExistingProductDirectory\(Dir\)" `
    "Installer must allow only missing directories, empty directories, or existing product directories."

Assert-MatchesRegex `
    $content `
    "procedure\s+DeletePreviousInstallManifestFiles[\s\S]*?IsSafeInstallManifestPath\(RelativePath\)[\s\S]*?not ShouldPreserveInstallMarker\(RelativePath\)[\s\S]*?DeleteFile\(TargetPath\)" `
    "Manifest cleanup must validate paths, preserve markers, and delete stale files."

$installDeleteSection = [regex]::Match($content, "(?ms)^\[InstallDelete\]\s*(?<body>.*?)(?=^\[)")
if (!$installDeleteSection.Success) {
    throw "Installer static check failed: InstallDelete section could not be parsed."
}

$unprotectedInstallDeleteLines = @(
    $installDeleteSection.Groups["body"].Value -split "\r?\n" |
        Where-Object { $_.TrimStart().StartsWith("Type:", [System.StringComparison]::OrdinalIgnoreCase) } |
        Where-Object { $_.IndexOf("Check: ShouldCleanExistingProductDirectory", [System.StringComparison]::OrdinalIgnoreCase) -lt 0 }
)
if ($unprotectedInstallDeleteLines.Count -gt 0) {
    throw "Installer static check failed: every InstallDelete entry must be protected by ShouldCleanExistingProductDirectory. $($unprotectedInstallDeleteLines -join '; ')"
}

$result = [pscustomobject]@{
    InstallerScriptPath = $installerScriptFull
    CheckedAt = (Get-Date).ToString("o")
    Passed = $true
}

$result | ConvertTo-Json
