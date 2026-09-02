<#
.SYNOPSIS
    Gate E5B: registers/unregisters the TEMPORARY "com.privon.devmeasure" Native Messaging host for
    exactly one browser at a time (Chrome first), so the real machine's Chrome/Edge can launch
    tools/Privon.WebLaunchMeasurement and its launch shape can be measured.

.DESCRIPTION
    This script is prepared as part of Gate E5B-PREP but is NOT executed during PREP -- no registry
    key and no file under %LOCALAPPDATA%\PRIVON-DEVMEASURE is created until a human explicitly runs
    -Action Setup as the next, separate step.

    Scope, absolutely:
      - HKCU only. Never HKLM, never WOW6432Node.
      - Host name "com.privon.devmeasure" only. Never touches "com.privon.host" (the real
        production host name) or any production manifest/registration.
      - Chrome subkey: HKCU\Software\Google\Chrome\NativeMessagingHosts\com.privon.devmeasure
      - Edge subkey:   HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.privon.devmeasure
      - Never enumerates or touches sibling keys under NativeMessagingHosts, and never deletes the
        NativeMessagingHosts parent key itself.
      - Cleanup never deletes anything without first verifying THIS run created it (ownership
        check against a crash-recovery intent record written before any mutation). A foreign or
        externally-mutated registration is reported, never silently deleted.
      - Supports one browser at a time -- Chrome must be fully cleaned before Edge setup begins,
        since both would otherwise share %LOCALAPPDATA%\PRIVON-DEVMEASURE.

.PARAMETER Action
    "Setup" or "Cleanup".

.PARAMETER Browser
    "Chrome" or "Edge". Chrome is measured first; no Edge action until Chrome is fully cleaned.

.PARAMETER ExtensionId
    Required for -Action Setup only. The real, already-loaded unpacked dev extension's ID as shown
    by chrome://extensions / edge://extensions after loading tools/Privon.WebLaunchMeasurement/DevExtension.
    This script never invents or guesses an extension ID.

.PARAMETER HostExecutablePath
    Required for -Action Setup only. Absolute path to the built Privon.WebLaunchMeasurement.exe
    (e.g. tools\Privon.WebLaunchMeasurement\bin\Debug\net10.0-windows\Privon.WebLaunchMeasurement.exe).

.NOTES
    Dependency-free: built-in PowerShell registry provider (HKCU:\...) and filesystem cmdlets only.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Setup", "Cleanup")]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [ValidateSet("Chrome", "Edge")]
    [string]$Browser,

    [string]$ExtensionId,

    [string]$HostExecutablePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Info {
    param([string]$Message)
    Write-Host "    $Message"
}

function Fail {
    param([string]$Code, [string]$Message)
    Write-Host ""
    Write-Host "${Code}: $Message" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# Constants -- deliberately hardcoded, never derived from user input, so this
# script can never be redirected at a production key/path/host name.
# ---------------------------------------------------------------------------

$HostName = "com.privon.devmeasure"

$BrowserRegistryRoot = @{
    Chrome = "HKCU:\Software\Google\Chrome\NativeMessagingHosts"
    Edge   = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts"
}

$BrowserManifestFileName = @{
    Chrome = "chrome.json"
    Edge   = "edge.json"
}

$DevMeasureRoot = Join-Path $env:LOCALAPPDATA "PRIVON-DEVMEASURE"
$EvidenceDir    = Join-Path $DevMeasureRoot "evidence"

# Intent (crash-recovery) records live OUTSIDE $DevMeasureRoot deliberately, so the record proving
# what this run intended to create/own survives independently of whatever happened to
# $DevMeasureRoot itself (created, partially created, or never created).
$IntentRoot = Join-Path $env:LOCALAPPDATA "PRIVON-DEVMEASURE-INTENT"
$IntentPath = Join-Path $IntentRoot "intent-$($Browser.ToLowerInvariant()).json"

$SubkeyPath  = Join-Path $BrowserRegistryRoot[$Browser] $HostName
$ManifestPath = Join-Path $DevMeasureRoot $BrowserManifestFileName[$Browser]

# ---------------------------------------------------------------------------
# Gate E5B-EDGE-SETUP.1 -- root-state compatibility between Setup and Cleanup.
#
# Cleanup (below) deliberately leaves $DevMeasureRoot in place when it still contains accepted
# measurement evidence -- that is CONTRACT-COMPLIANT residue, not staleness. Setup must therefore
# accept exactly that one shape as a valid starting state, while still failing closed on anything
# else (an activation artifact like chrome.json/edge.json, unknown residue, or a reparse point).
# This function inspects ONLY the immediate root shape -- it never inspects, deletes, or rewrites
# anything inside evidence/ itself.
# ---------------------------------------------------------------------------

function Test-DevMeasureRootIsExactEvidenceOnly {
    if (-not (Test-Path -LiteralPath $DevMeasureRoot)) {
        return $true
    }

    $rootItem = Get-Item -LiteralPath $DevMeasureRoot -Force
    if (-not $rootItem.PSIsContainer) {
        return $false
    }
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        return $false
    }

    # @(...) forces array context so .Count is always a well-defined 0/1/N under
    # Set-StrictMode -Version Latest -- see the identical fix in Invoke-Cleanup below.
    $topLevel = @(Get-ChildItem -LiteralPath $DevMeasureRoot -Force)
    if ($topLevel.Count -ne 1) {
        return $false
    }

    $onlyEntry = $topLevel[0]
    if ($onlyEntry.Name -ne "evidence") {
        return $false
    }
    if (-not $onlyEntry.PSIsContainer) {
        return $false
    }
    if (($onlyEntry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        return $false
    }

    return $true
}

# ---------------------------------------------------------------------------
# Setup
# ---------------------------------------------------------------------------

function Invoke-Setup {
    Write-Step "Gate E5B devmeasure setup -- Browser=$Browser"

    # 1. verify devmeasure host subkey absent
    if (Test-Path -LiteralPath $SubkeyPath) {
        Fail "STALE_DEVMEASURE_STATE" "Registry subkey already exists: $SubkeyPath -- run -Action Cleanup first. Not overwriting."
    }

    # 2. verify temporary directory is either absent, or exists as EXACT accepted-evidence-only
    #    residue from a prior correct Cleanup -- any other shape (an activation artifact, unknown
    #    residue, or a reparse point) fails closed exactly as before.
    if (-not (Test-DevMeasureRootIsExactEvidenceOnly)) {
        Fail "STALE_DEVMEASURE_STATE" "$DevMeasureRoot exists and is not exact evidence-only residue -- run -Action Cleanup first (for whichever browser previously set it up), or investigate foreign/unknown content. Not overwriting."
    }
    if (Test-Path -LiteralPath $IntentPath) {
        Fail "STALE_DEVMEASURE_STATE" "Intent record already exists: $IntentPath -- a previous $Browser setup was not cleaned up. Run -Action Cleanup for $Browser first. Not overwriting."
    }

    # 3. validate supplied development extension ID (real Chrome/Edge extension ID shape: exactly
    #    32 characters drawn from the alphabet a-p -- mirrors NativeMessagingHostInvocation's own
    #    frozen pattern in src/Privon.App). Never invented here.
    if ([string]::IsNullOrWhiteSpace($ExtensionId)) {
        Fail "SETUP_INPUT_MISSING" "-ExtensionId is required for -Action Setup (the real ID from chrome://extensions after loading DevExtension unpacked). This script never invents one."
    }
    if ($ExtensionId -notmatch '^[a-p]{32}$') {
        Fail "SETUP_INPUT_INVALID" "-ExtensionId '$ExtensionId' does not match the real extension ID shape (32 chars, alphabet a-p)."
    }
    if ([string]::IsNullOrWhiteSpace($HostExecutablePath) -or -not (Test-Path -LiteralPath $HostExecutablePath -PathType Leaf)) {
        Fail "SETUP_INPUT_INVALID" "-HostExecutablePath must point at an existing, already-built Privon.WebLaunchMeasurement.exe. Got: '$HostExecutablePath'"
    }
    $HostExecutablePath = (Resolve-Path -LiteralPath $HostExecutablePath).Path

    # 4. create intent record BEFORE mutation (crash-recovery journal, kept outside $DevMeasureRoot).
    New-Item -ItemType Directory -Path $IntentRoot -Force | Out-Null
    $intent = [ordered]@{
        runId               = [guid]::NewGuid().ToString()
        browser             = $Browser
        createdUtc          = (Get-Date).ToUniversalTime().ToString("o")
        subkeyPath          = $SubkeyPath
        manifestPath        = $ManifestPath
        devMeasureRoot      = $DevMeasureRoot
        extensionId         = $ExtensionId
        hostExecutablePath  = $HostExecutablePath
    }
    ($intent | ConvertTo-Json) | Set-Content -LiteralPath $IntentPath -Encoding utf8
    Write-Info "Intent record written: $IntentPath"

    # 5. create temporary directory
    New-Item -ItemType Directory -Path $DevMeasureRoot -Force | Out-Null
    Write-Info "Created: $DevMeasureRoot"

    # 6. create browser-specific manifest
    $manifest = [ordered]@{
        name             = $HostName
        description      = "PRIVON Gate E5B dev-only launch-shape measurement host. Non-shipping."
        path             = $HostExecutablePath
        type             = "stdio"
        allowed_origins  = @("chrome-extension://$ExtensionId/")
    }
    ($manifest | ConvertTo-Json) | Set-Content -LiteralPath $ManifestPath -Encoding utf8
    Write-Info "Created manifest: $ManifestPath"

    # 7. create exact HKCU devmeasure host subkey
    New-Item -Path $SubkeyPath -Force | Out-Null

    # 8. set default value to exact manifest path
    Set-Item -LiteralPath $SubkeyPath -Value $ManifestPath

    # 9. verify readback
    $readback = (Get-Item -LiteralPath $SubkeyPath).GetValue("")
    if ($readback -ne $ManifestPath) {
        Fail "SETUP_READBACK_MISMATCH" "Registry default value readback '$readback' != expected '$ManifestPath'."
    }
    Write-Info "Registry subkey created and verified: $SubkeyPath -> $ManifestPath"

    Write-Step "Setup complete for $Browser."
    Write-Info "Next manual step: load tools/Privon.WebLaunchMeasurement/DevExtension unpacked in $Browser and trigger connectNative."
}

# ---------------------------------------------------------------------------
# Cleanup
# ---------------------------------------------------------------------------

function Invoke-Cleanup {
    Write-Step "Gate E5B devmeasure cleanup -- Browser=$Browser"

    if (-not (Test-Path -LiteralPath $IntentPath)) {
        Write-Info "No intent record for $Browser at $IntentPath -- nothing for this script to clean up. Leaving all state untouched."
        return
    }

    $intent = Get-Content -LiteralPath $IntentPath -Raw | ConvertFrom-Json
    $ownedSubkeyPath   = $intent.subkeyPath
    $ownedManifestPath = $intent.manifestPath
    $ownedDevMeasureRoot = $intent.devMeasureRoot

    # 1 & 2. open exact devmeasure host subkey only; verify default value still equals the manifest
    # this run created.
    if (Test-Path -LiteralPath $ownedSubkeyPath) {
        $currentValue = (Get-Item -LiteralPath $ownedSubkeyPath).GetValue("")
        if ($currentValue -ne $ownedManifestPath) {
            Fail "FOREIGN_OR_MUTATED_DEV_REGISTRATION" "Subkey $ownedSubkeyPath default value is '$currentValue', expected '$ownedManifestPath' (this run's own value). NOT DELETING. Investigate manually."
        }

        # 3. delete exact subkey non-recursively only on ownership match.
        Remove-Item -LiteralPath $ownedSubkeyPath -Force
        Write-Info "Deleted registry subkey: $ownedSubkeyPath"
    }
    else {
        Write-Info "Registry subkey already absent: $ownedSubkeyPath"
    }

    # 4. delete exact manifest created by this run.
    if (Test-Path -LiteralPath $ownedManifestPath -PathType Leaf) {
        Remove-Item -LiteralPath $ownedManifestPath -Force
        Write-Info "Deleted manifest: $ownedManifestPath"
    }
    else {
        Write-Info "Manifest already absent: $ownedManifestPath"
    }

    # 5. delete temp directory only if empty and owned by this run.
    if (Test-Path -LiteralPath $ownedDevMeasureRoot) {
        # @(...) forces array context: Get-ChildItem returns a bare (non-array) FileSystemInfo when
        # exactly one item matches, and under Set-StrictMode -Version Latest that single object has
        # no .Count member -- a PropertyNotFoundException, observed for real once chrome.json had
        # already been deleted and exactly one item (evidence/) remained. Wrapping in @() guarantees
        # .Count is always a well-defined 0/1/N regardless of match count.
        $remaining = @(Get-ChildItem -LiteralPath $ownedDevMeasureRoot -Force)
        if ($remaining.Count -eq 0) {
            Remove-Item -LiteralPath $ownedDevMeasureRoot -Force
            Write-Info "Deleted empty directory: $ownedDevMeasureRoot"
        }
        else {
            Write-Host ""
            Write-Host "FOREIGN_OR_MUTATED_DEV_REGISTRATION: $ownedDevMeasureRoot is not empty (contains: $($remaining.Name -join ', ')) -- NOT DELETING the directory. Evidence files under evidence/ are expected here and are left in place; remove manually once reviewed." -ForegroundColor Yellow
        }
    }

    # 6, 7, 8. verify registry subkey / manifest / directory absent (directory check is informational
    # only if step 5 above intentionally left it in place due to non-empty content).
    $subkeyAbsent   = -not (Test-Path -LiteralPath $ownedSubkeyPath)
    $manifestAbsent = -not (Test-Path -LiteralPath $ownedManifestPath)
    Write-Info "Verify subkey absent: $subkeyAbsent"
    Write-Info "Verify manifest absent: $manifestAbsent"

    if (-not $subkeyAbsent -or -not $manifestAbsent) {
        Fail "CLEANUP_VERIFICATION_FAILED" "Post-cleanup verification failed (subkeyAbsent=$subkeyAbsent manifestAbsent=$manifestAbsent)."
    }

    # Only remove the intent record (and its root, if now empty) once subkey+manifest are confirmed
    # gone. The directory may legitimately remain if evidence files are still present -- that is not
    # a cleanup failure, just left for the human to review/delete.
    Remove-Item -LiteralPath $IntentPath -Force
    # Same @() array-context fix as above -- otherwise a single remaining sibling intent file (or
    # $IntentRoot becoming empty) hits the identical StrictMode singleton/Count defect.
    $remainingIntents = @(Get-ChildItem -LiteralPath $IntentRoot -Force)
    if ($remainingIntents.Count -eq 0) {
        Remove-Item -LiteralPath $IntentRoot -Force
    }

    Write-Step "Cleanup complete for $Browser."
}

switch ($Action) {
    "Setup"   { Invoke-Setup }
    "Cleanup" { Invoke-Cleanup }
}
