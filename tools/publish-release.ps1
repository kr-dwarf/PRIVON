<#
.SYNOPSIS
    Builds and packages the PRIVON 0.1 public beta release ZIP for win-x64.

.DESCRIPTION
    Wraps the verified `dotnet publish` command (self-contained, single-file, win-x64,
    no PDBs) for Privon.App, stages the output, renames the entry executable to
    PRIVON.exe (staged file only -- csproj AssemblyName is left untouched), rejects any
    unexpected artifact (PDBs, test binaries, dev-tool/server content, diagnostic logs,
    local user data files), and produces:

        release/PRIVON-<version>-win-x64.zip
        release/PRIVON-<version>-win-x64.zip.sha256

    This script only ever creates/cleans its own `release/` staging directory. It never
    deletes or modifies any other repository content.

.NOTES
    Dependency-free: uses only built-in PowerShell/.NET (Compress-Archive, Get-FileHash).
    Run from anywhere -- paths are resolved relative to this script's location.
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$Rid = "win-x64"
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
    param([string]$Message)
    Write-Host ""
    Write-Host "FAILED: $Message" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# 0. Explicit version required. There is no default -- a previous stale
#    default ("0.1.0-beta") let a 0.2 build be silently packaged and labeled
#    as the old 0.1 release. Replacing that default with a new hardcoded
#    value (e.g. "0.2.0-beta") would only move the identical problem to the
#    next release, so this parameter has no default at all: every invocation
#    must explicitly state the version it is packaging. This check runs
#    before any path resolution or filesystem work.
# ---------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($Version)) {
    Fail "Explicit -Version is required (e.g. -Version `"0.2.0-beta`"). There is no default -- this prevents a release artifact from ever being silently mislabeled with a stale version string."
}

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot   = Split-Path -Parent $ScriptDir
$AppProject = Join-Path $RepoRoot "src\Privon.App\Privon.App.csproj"

$ReleaseDir = Join-Path $RepoRoot "release"
$StagingDir = Join-Path $ReleaseDir "staging"

$PackageName = "PRIVON-$Version-$Rid"
$ZipPath     = Join-Path $ReleaseDir "$PackageName.zip"
$Sha256Path  = "$ZipPath.sha256"

if (-not (Test-Path $AppProject)) {
    Fail "Privon.App.csproj not found at expected path: $AppProject"
}

Write-Step "PRIVON release packaging"
Write-Info "Repo root : $RepoRoot"
Write-Info "Project   : $AppProject"
Write-Info "Version   : $Version"
Write-Info "RID       : $Rid"
Write-Info "Release   : $ReleaseDir"

# ---------------------------------------------------------------------------
# 0b. Resolve the exact Git HEAD this invocation is packaging. Captured once,
#     up front, and used later (0c, and again after publish) as the single
#     source of provenance truth for this run -- never re-read or re-derived.
# ---------------------------------------------------------------------------

Write-Step "Resolving Git HEAD"

$capturedHeadRaw = (& git -C $RepoRoot rev-parse HEAD 2>&1)
if ($LASTEXITCODE -ne 0) {
    Fail "Unable to resolve Git HEAD via 'git rev-parse HEAD' in $RepoRoot. Ensure this script is run inside a valid Git working tree."
}

$CapturedHead = $capturedHeadRaw.Trim()
if ($CapturedHead -notmatch '^[0-9a-f]{40}$') {
    Fail "Resolved Git HEAD does not look like a full 40-character SHA: '$CapturedHead'"
}

Write-Info "Captured HEAD: $CapturedHead"

# ---------------------------------------------------------------------------
# 0c. Refuse to package from a dirty working tree. `release/` is already
#     gitignored, so its own generated content never appears in `git status`
#     output -- no path is special-cased here. Fails closed: never auto-
#     stashes, auto-resets, or otherwise mutates repository/user state.
# ---------------------------------------------------------------------------

Write-Step "Verifying clean working tree"

$gitStatus = (& git -C $RepoRoot status --porcelain 2>&1)
if ($LASTEXITCODE -ne 0) {
    Fail "Unable to run 'git status --porcelain' in $RepoRoot. Ensure this script is run inside a valid Git working tree."
}

if ($gitStatus) {
    Write-Host ""
    Write-Host "Working tree is not clean:" -ForegroundColor Red
    foreach ($line in $gitStatus) {
        Write-Host "  $line" -ForegroundColor Red
    }
    Fail "Refusing to package a release artifact from a dirty working tree. Commit, stash, or discard local changes first."
}

Write-Info "Working tree is clean."

# ---------------------------------------------------------------------------
# 1. Clean only this script's own release staging/output directory.
#    Nothing else in the repository is touched or deleted.
# ---------------------------------------------------------------------------

Write-Step "Cleaning release staging directory (release/ only)"
if (Test-Path $ReleaseDir) {
    Remove-Item -Path $ReleaseDir -Recurse -Force
}
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null
Write-Info "Recreated: $ReleaseDir"

# ---------------------------------------------------------------------------
# 2. Run the verified publish command.
# ---------------------------------------------------------------------------

Write-Step "Publishing Privon.App ($Rid, self-contained, single-file, no PDB)"

& dotnet publish $AppProject `
    -c Release `
    -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -o $StagingDir

if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish exited with code $LASTEXITCODE"
}

# ---------------------------------------------------------------------------
# 3. Verify expected public runtime files exist.
# ---------------------------------------------------------------------------

Write-Step "Verifying publish output"

$publishedExe = Join-Path $StagingDir "Privon.App.exe"
if (-not (Test-Path $publishedExe)) {
    Fail "Expected published executable not found: $publishedExe"
}

$stagedFiles = Get-ChildItem -Path $StagingDir -Recurse -File
if ($stagedFiles.Count -eq 0) {
    Fail "Publish output directory is empty: $StagingDir"
}

Write-Info "Publish produced $($stagedFiles.Count) file(s):"
foreach ($f in ($stagedFiles | Sort-Object Name)) {
    $relPath = $f.FullName.Substring($StagingDir.Length + 1)
    Write-Info ("  - {0}  ({1:N0} bytes)" -f $relPath, $f.Length)
}

# ---------------------------------------------------------------------------
# 3b. Verify the executable THIS invocation just published actually embeds
#     the exact Git HEAD captured in step 0b -- using only the .NET SDK's
#     existing, zero-configuration InformationalVersion/ProductVersion
#     source-revision embedding (no new provenance mechanism, no manifest,
#     no network call). This is checked on $publishedExe (the freshly
#     produced Privon.App.exe, before the rename below) so there is no
#     ambiguity about which file is being verified -- it cannot be an old
#     staging leftover, since step 1 unconditionally wiped and step 2
#     unconditionally re-published this exact directory moments ago. No
#     timestamp, filename, or file-existence check is treated as sufficient
#     provenance -- only the embedded revision string itself. A failure here
#     aborts before the forbidden-artifact scan, the rename, the ZIP, and the
#     checksum -- no package success can ever be reported past this point.
# ---------------------------------------------------------------------------

Write-Step "Verifying embedded Git HEAD provenance"

$publishedVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe)
$embeddedProductVersion = $publishedVersionInfo.ProductVersion

if ([string]::IsNullOrWhiteSpace($embeddedProductVersion)) {
    Fail "Published executable has no ProductVersion metadata -- cannot verify release provenance: $publishedExe"
}

Write-Info "Embedded ProductVersion: $embeddedProductVersion"

if ($embeddedProductVersion -notlike "*$CapturedHead*") {
    Fail (
        "Release artifact provenance check FAILED. Expected the published executable's " +
        "ProductVersion to contain the exact Git HEAD captured at the start of this " +
        "invocation ($CapturedHead), but it did not (ProductVersion: '$embeddedProductVersion'). " +
        "Refusing to package -- a release artifact must never ship without exact source " +
        "provenance."
    )
}

Write-Info "Provenance verified: embedded ProductVersion contains expected HEAD ($CapturedHead)."

# ---------------------------------------------------------------------------
# 4. Reject unexpected/forbidden artifacts BEFORE renaming/zipping.
#    This is a blocklist, not an allowlist -- native runtime file sets can
#    legitimately vary across SDK/runtime versions, so we don't hardcode an
#    exact allowed list. We only hard-fail on content that must never ship.
# ---------------------------------------------------------------------------

Write-Step "Scanning for forbidden artifacts"

$forbiddenPatterns = @(
    "*.pdb",
    "*Tests*.dll",
    "*Tests*.exe",
    "*testhost*",
    "*xunit*",
    "*UiaInspector*",
    "privon-diagnostic-*.log",
    "master.key",
    "settings.bin",
    "trusted-public-info.bin",
    "exceptions.bin"
)

$violations = @()
foreach ($pattern in $forbiddenPatterns) {
    $matches = $stagedFiles | Where-Object { $_.Name -like $pattern }
    if ($matches) {
        $violations += $matches
    }
}

# Also reject anything that looks like server/ or tools/ content leaking into
# the publish output (would indicate a broken project reference/publish scope).
$pathViolations = $stagedFiles | Where-Object {
    $_.FullName -match '\\server\\' -or $_.FullName -match '\\tools\\'
}
if ($pathViolations) {
    $violations += $pathViolations
}

if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "Forbidden artifact(s) found in publish output:" -ForegroundColor Red
    foreach ($v in ($violations | Select-Object -Unique)) {
        Write-Host "  - $($v.FullName)" -ForegroundColor Red
    }
    Fail "Publish output contains forbidden artifacts. Aborting before packaging."
}

Write-Info "No forbidden artifacts found."

# ---------------------------------------------------------------------------
# 5. Rename the staged executable only. csproj AssemblyName is NOT touched.
# ---------------------------------------------------------------------------

Write-Step "Renaming staged executable: Privon.App.exe -> PRIVON.exe"

$finalExe = Join-Path $StagingDir "PRIVON.exe"
Move-Item -Path $publishedExe -Destination $finalExe -Force
Write-Info "Staged: $finalExe"

if (Test-Path $publishedExe) {
    Fail "Privon.App.exe still present after rename -- refusing to package."
}
if (-not (Test-Path $finalExe)) {
    Fail "PRIVON.exe missing after rename -- refusing to package."
}

# ---------------------------------------------------------------------------
# 6. Package the ZIP.
# ---------------------------------------------------------------------------

Write-Step "Creating release ZIP"

if (Test-Path $ZipPath) {
    Remove-Item -Path $ZipPath -Force
}

$itemsToZip = Get-ChildItem -Path $StagingDir -Force
Compress-Archive -Path $itemsToZip.FullName -DestinationPath $ZipPath -CompressionLevel Optimal

if (-not (Test-Path $ZipPath)) {
    Fail "ZIP was not created: $ZipPath"
}

$zipSizeBytes = (Get-Item $ZipPath).Length
Write-Info "Created: $ZipPath"
Write-Info ("Size: {0:N0} bytes ({1:N2} MB)" -f $zipSizeBytes, ($zipSizeBytes / 1MB))

# ---------------------------------------------------------------------------
# 7. Create the SHA-256 checksum file (no BOM, sha256sum-compatible format).
# ---------------------------------------------------------------------------

Write-Step "Computing SHA-256 checksum"

$hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipFileName = Split-Path -Leaf $ZipPath
$sha256Line = "$hash  $zipFileName"

# Write without a BOM so the checksum file is portable to non-Windows tooling.
[System.IO.File]::WriteAllText($Sha256Path, "$sha256Line`n", [System.Text.UTF8Encoding]::new($false))

Write-Info "SHA-256: $hash"
Write-Info "Written: $Sha256Path"

# ---------------------------------------------------------------------------
# 8. Self-verify the checksum we just wrote actually matches the ZIP.
# ---------------------------------------------------------------------------

Write-Step "Self-verifying checksum"

$recomputed = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($recomputed -ne $hash) {
    Fail "Checksum mismatch immediately after writing .sha256 -- this should be impossible."
}
Write-Info "Checksum verified OK."

# ---------------------------------------------------------------------------
# Done.
# ---------------------------------------------------------------------------

Write-Step "Release package complete"
Write-Info "ZIP     : $ZipPath"
Write-Info "SHA-256 : $Sha256Path"
Write-Host ""
