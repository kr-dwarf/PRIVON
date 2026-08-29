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
    Dependency-free: uses only built-in PowerShell/.NET (Compress-Archive, Get-FileHash)
    plus `git worktree` (BUG-005 fix, see step 0d below). Run from anywhere -- paths are
    resolved relative to this script's location.
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
$SourceAppProject = Join-Path $RepoRoot "src\Privon.App\Privon.App.csproj"

$ReleaseDir = Join-Path $RepoRoot "release"
$StagingDir = Join-Path $ReleaseDir "staging"

$PackageName = "PRIVON-$Version-$Rid"
$ZipPath     = Join-Path $ReleaseDir "$PackageName.zip"
$Sha256Path  = "$ZipPath.sha256"

if (-not (Test-Path $SourceAppProject)) {
    Fail "Privon.App.csproj not found at expected path: $SourceAppProject"
}

Write-Step "PRIVON release packaging"
Write-Info "Repo root : $RepoRoot"
Write-Info "Project   : $SourceAppProject"
Write-Info "Version   : $Version"
Write-Info "RID       : $Rid"
Write-Info "Release   : $ReleaseDir"

# ---------------------------------------------------------------------------
# 0b. Resolve the exact Git HEAD this invocation is packaging. Captured once,
#     up front, and used for the REST of this script as the single source of
#     provenance truth -- never re-read or re-derived. BUG-005 (frozen):
#     $CapturedHead is the ONLY commit identity this script ever trusts --
#     it selects the isolated worktree (step 0d), is passed explicitly as
#     the embedded SourceRevisionId (step 2), and is what step 3b verifies
#     against. No step downstream of this one is ever allowed to introduce
#     a second, competing notion of "which commit."
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
# 0c. Refuse to package from a dirty PRIMARY working tree. `release/` is
#     already gitignored, so its own generated content never appears in
#     `git status` output -- no path is special-cased here. Fails closed:
#     never auto-stashes, auto-resets, or otherwise mutates repository/user
#     state.
#
#     BUG-005 (S2 CONFIRMED, closed by step 0d below): this check alone is
#     NOT sufficient release-source-integrity evidence -- it only proves the
#     TRACKED primary working tree was clean at THIS instant. It does not,
#     and structurally cannot, prevent that same tree from being mutated
#     later, while the build in step 2 is still reading it (a real,
#     demonstrated TOCTOU: a file edited after this check passes and
#     restored before the script finishes leaves the tree clean again while
#     the produced artifact still contains the mutated content and still
#     claims $CapturedHead). This check is therefore retained as a cheap,
#     fast-failing early guard for the ordinary "I forgot to commit" case,
#     but the actual integrity guarantee comes from step 0d building from an
#     isolated, detached worktree pinned to CapturedHead -- never from
#     re-checking this same mutable tree again later (a second/third clean
#     check would have the identical mutate-then-restore blind spot).
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
# 0d. BUG-005 fix -- materialize an ISOLATED, DETACHED git worktree at
#     EXACTLY $CapturedHead, and build exclusively from it. This is the
#     actual integrity guarantee: from this point on, the primary working
#     tree at $RepoRoot is NEVER read by the build again (only $ReleaseDir/
#     $StagingDir under it are still used, as a plain output destination,
#     which was never the vulnerable resource). A worktree's files come
#     directly from git's own immutable, content-addressed object store --
#     there is no code path by which a concurrent edit to the primary tree
#     can reach them, whether or not that edit is later reverted.
#
#     UNIQUE_PATH (per-invocation, never reused): a fresh GUID-suffixed
#     directory under the user's TEMP folder, so concurrent or successive
#     invocations can never collide and this script never depends on -- or
#     leaves behind -- a persistent "the release worktree".
#
#     OWNERSHIP_SCOPED_CLEANUP (frozen): this script removes ONLY the exact
#     worktree path IT created, in a `finally` block covering every
#     remaining step, regardless of success or failure. It never runs
#     `git worktree prune` or touches any other worktree registration --
#     that is broader maintenance this release operation does not own.
#
#     `--detach` at the explicit commit SHA (never a branch name): the
#     worktree's HEAD can never be moved by an unrelated `git checkout`/
#     `git pull` elsewhere, and does not create or touch any branch ref.
# ---------------------------------------------------------------------------

Write-Step "Creating isolated release worktree at $CapturedHead"

$TempWorktreeDir = Join-Path $env:TEMP ("privon-release-" + [Guid]::NewGuid().ToString("N"))
$WorktreeCreated = $false
$CleanupFailed = $false

try {
    & git -C $RepoRoot worktree add --detach $TempWorktreeDir $CapturedHead
    if ($LASTEXITCODE -ne 0) {
        Fail "Unable to create an isolated git worktree at $CapturedHead ($TempWorktreeDir). Refusing to build from the mutable primary working tree."
    }
    $WorktreeCreated = $true
    Write-Info "Isolated worktree: $TempWorktreeDir"

    $AppProject = Join-Path $TempWorktreeDir "src\Privon.App\Privon.App.csproj"
    if (-not (Test-Path $AppProject)) {
        Fail "Privon.App.csproj not found in the isolated worktree at expected path: $AppProject"
    }

    # -----------------------------------------------------------------------
    # 1. RPT-010 correction: clean ONLY this run's own disposable/current-
    #    version output -- $StagingDir (always disposable, rebuilt fresh
    #    every run) and the EXACT current-version $ZipPath/$Sha256Path (so a
    #    stale same-version ZIP/checksum left over from an earlier failed or
    #    aborted run can never be mistaken for this run's fresh success).
    #    Previously this step recursively deleted the ENTIRE $ReleaseDir --
    #    which silently destroyed every differently-versioned historical
    #    artifact (release/PRIVON-<older-version>-win-x64.zip and its own
    #    .sha256) sitting alongside it, with no way to recover them short of
    #    re-packaging that older version from source. Nothing else under
    #    $ReleaseDir is ever touched or deleted by this step -- no wildcard,
    #    no directory-wide removal; the preservation of every other file is
    #    structural (this step only ever names $StagingDir/$ZipPath/
    #    $Sha256Path), never a naming-convention accident. A fresh worktree
    #    also starts with no bin/obj of its own (git never tracks them --
    #    see .gitignore), so this step and step 0d together still guarantee
    #    no stale/incremental BUILD output, in either the source or the
    #    destination, can survive into this run.
    #
    #    STALE_OUTPUT_TIMING (frozen): both exact current-version outputs are
    #    removed HERE, before publish even runs -- never delayed until after
    #    a successful ZIP/checksum. If this run fails anywhere between here
    #    and checksum self-verification, there is no old same-version
    #    ZIP/checksum left behind that could look like this run's own
    #    (possibly nonexistent) success.
    # -----------------------------------------------------------------------

    Write-Step "Cleaning this run's own disposable/current-version output"
    if (Test-Path $StagingDir) {
        Remove-Item -Path $StagingDir -Recurse -Force
    }
    if (Test-Path $ZipPath) {
        Remove-Item -Path $ZipPath -Force
    }
    if (Test-Path $Sha256Path) {
        Remove-Item -Path $Sha256Path -Force
    }
    if (-not (Test-Path $ReleaseDir)) {
        New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null
    }
    New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null
    Write-Info "Cleared (if present): $StagingDir, $(Split-Path -Leaf $ZipPath), $(Split-Path -Leaf $Sha256Path)"
    Write-Info "Recreated: $StagingDir"

    # -----------------------------------------------------------------------
    # 2. Run the verified publish command -- against the ISOLATED worktree's
    #    own copy of the project (never $SourceAppProject / $RepoRoot).
    #
    #    RPT-006 correction: -Version previously controlled ONLY the ZIP/
    #    staging file naming below -- it was never passed into the actual
    #    MSBuild/publish invocation, so the project's own default Version
    #    (1.0.0, since Privon.App.csproj declares none) is what actually got
    #    embedded, regardless of what version this script's caller requested.
    #    A package named for e.g. v0.2.1 could therefore ship an executable
    #    whose own version metadata still read 1.0.0. -p:Version=$Version
    #    now makes the REQUESTED release version the one MSBuild actually
    #    builds with, and -p:InformationalVersion explicitly combines it with
    #    the captured HEAD so the single ProductVersion string this script
    #    already inspects (step 3b below) deterministically carries BOTH
    #    facts this script exists to prove -- not left to the SDK's own
    #    default SourceRevisionId-appending behavior, which is versioned
    #    tooling the script no longer needs to rely on agreeing.
    #
    #    -p:SourceRevisionId=$CapturedHead (BUG-005 fix) is kept unchanged --
    #    it remains the single source of provenance truth this whole script
    #    already trusts, and continues to be embedded even though
    #    -p:InformationalVersion above no longer needs its own auto-append to
    #    do so.
    # -----------------------------------------------------------------------

    Write-Step "Publishing Privon.App ($Rid, self-contained, single-file, no PDB)"

    & dotnet publish $AppProject `
        -c Release `
        -r $Rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=false `
        -p:DebugType=None `
        -p:SourceRevisionId=$CapturedHead `
        -p:Version=$Version `
        -p:InformationalVersion="$Version+$CapturedHead" `
        -p:IncludeSourceRevisionInInformationalVersion=false `
        -o $StagingDir

    if ($LASTEXITCODE -ne 0) {
        Fail "dotnet publish exited with code $LASTEXITCODE"
    }

    # -----------------------------------------------------------------------
    # 3. Verify expected public runtime files exist.
    # -----------------------------------------------------------------------

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

    # -----------------------------------------------------------------------
    # 3b. Verify the executable THIS invocation just published actually
    #     embeds the exact Git HEAD captured in step 0b -- using only the
    #     .NET SDK's existing, zero-configuration InformationalVersion/
    #     ProductVersion source-revision embedding (no new provenance
    #     mechanism, no manifest, no network call). This is checked on
    #     $publishedExe (the freshly produced Privon.App.exe, before the
    #     rename below) so there is no ambiguity about which file is being
    #     verified -- it cannot be an old staging leftover, since step 1
    #     unconditionally wiped and step 2 unconditionally re-published this
    #     exact directory moments ago, from the isolated worktree. No
    #     timestamp, filename, or file-existence check is treated as
    #     sufficient provenance -- only the embedded revision string itself.
    #     A failure here aborts before the forbidden-artifact scan, the
    #     rename, the ZIP, and the checksum -- no package success can ever
    #     be reported past this point.
    #
    #     BUG-005 (frozen): this step is UNCHANGED from before the fix, and
    #     deliberately still compares against $CapturedHead -- never against
    #     a value re-derived from the worktree. A verification query MAY
    #     confirm the worktree is at $CapturedHead (see step 0d), but
    #     $CapturedHead itself is never replaced by a later reading.
    # -----------------------------------------------------------------------

    Write-Step "Verifying embedded Git HEAD provenance"

    $publishedVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe)
    $embeddedProductVersion = $publishedVersionInfo.ProductVersion

    if ([string]::IsNullOrWhiteSpace($embeddedProductVersion)) {
        Fail "Published executable has no ProductVersion metadata -- cannot verify release provenance: $publishedExe"
    }

    Write-Info "Embedded ProductVersion: $embeddedProductVersion"

    # RPT-006 correction: EXACT equality, never substring matching. A substring check (the previous
    # "-notlike '*$CapturedHead*'" / "-notlike '*$Version*'" pair) can be satisfied by unrelated
    # metadata that merely happens to CONTAIN the requested version or SHA as a substring of
    # something else entirely -- e.g. a requested version of "0.2.1" would substring-match an
    # unrelated ProductVersion of "10.2.10+<sha>", which shares no actual identity with "0.2.1" at
    # all. That is coincidence, not provenance. Because this script's own step 2 explicitly sets
    # -p:InformationalVersion="$Version+$CapturedHead" (with SourceRevisionId auto-append disabled),
    # the embedded ProductVersion has exactly one correct value -- so this check compares against it
    # with case-sensitive exact string equality, not a pattern that could also match other content.
    # This single check supersedes and replaces the two weaker substring checks previously here --
    # it is the sole release-correctness gate for both facts (requested version AND captured SHA),
    # not an additional check layered alongside them.
    $expectedProductVersion = "$Version+$CapturedHead"

    if ($embeddedProductVersion -cne $expectedProductVersion) {
        Fail (
            "Release artifact provenance check FAILED. Expected the published executable's " +
            "ProductVersion to equal EXACTLY '$expectedProductVersion' (requested release version " +
            "'$Version' + captured source SHA '$CapturedHead'), but the actual embedded value was " +
            "'$embeddedProductVersion'. Refusing to package -- a release artifact must never ship " +
            "without exact source provenance and exact version metadata."
        )
    }

    Write-Info "Provenance verified: embedded ProductVersion exactly equals '$expectedProductVersion' (requested version '$Version' + captured HEAD '$CapturedHead')."

    # -----------------------------------------------------------------------
    # 4. Reject unexpected/forbidden artifacts BEFORE renaming/zipping.
    #    This is a blocklist, not an allowlist -- native runtime file sets
    #    can legitimately vary across SDK/runtime versions, so we don't
    #    hardcode an exact allowed list. We only hard-fail on content that
    #    must never ship.
    # -----------------------------------------------------------------------

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
        "exceptions.bin",
        "user-exceptions.bin"
    )

    $violations = @()
    foreach ($pattern in $forbiddenPatterns) {
        $matches = $stagedFiles | Where-Object { $_.Name -like $pattern }
        if ($matches) {
            $violations += $matches
        }
    }

    # Also reject anything that looks like server/ or tools/ content leaking
    # into the publish output (would indicate a broken project reference/
    # publish scope).
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

    # -----------------------------------------------------------------------
    # 5. Rename the staged executable only. csproj AssemblyName is NOT
    #    touched.
    # -----------------------------------------------------------------------

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

    # -----------------------------------------------------------------------
    # 6. Package the ZIP.
    # -----------------------------------------------------------------------

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

    # -----------------------------------------------------------------------
    # 7. Create the SHA-256 checksum file (no BOM, sha256sum-compatible
    #    format).
    # -----------------------------------------------------------------------

    Write-Step "Computing SHA-256 checksum"

    $hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipFileName = Split-Path -Leaf $ZipPath
    $sha256Line = "$hash  $zipFileName"

    # Write without a BOM so the checksum file is portable to non-Windows tooling.
    [System.IO.File]::WriteAllText($Sha256Path, "$sha256Line`n", [System.Text.UTF8Encoding]::new($false))

    Write-Info "SHA-256: $hash"
    Write-Info "Written: $Sha256Path"

    # -----------------------------------------------------------------------
    # 8. Self-verify the checksum we just wrote actually matches the ZIP.
    # -----------------------------------------------------------------------

    Write-Step "Self-verifying checksum"

    $recomputed = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($recomputed -ne $hash) {
        Fail "Checksum mismatch immediately after writing .sha256 -- this should be impossible."
    }
    Write-Info "Checksum verified OK."

    # -----------------------------------------------------------------------
    # Done (packaging steps only). BUG-005-CLEANUP-001: the "Release package
    # complete" success message is deliberately NOT printed here -- it is
    # printed after the try/finally below, and only once worktree cleanup
    # (in `finally`) is confirmed to have actually succeeded. Reporting
    # success here, before cleanup runs, is exactly the defect that let a
    # failed cleanup finish silently.
    # -----------------------------------------------------------------------
} finally {
    # BUG-005 (frozen): OWNERSHIP_SCOPED_CLEANUP -- removes ONLY the exact
    # worktree this invocation created, regardless of whether the try block
    # above succeeded, called Fail() (which exits via a plain `exit` and
    # still unwinds through this finally), or threw. Never `git worktree
    # prune` (that is unrelated, global maintenance this release operation
    # does not own) and never any other worktree path. `git worktree remove`
    # is the only operation that clears git's own worktree registration, not
    # just the checked-out directory -- so it, not the best-effort manual
    # fallback below, is what $CleanupFailed is based on: a manual directory
    # delete can tidy up disk space, but it cannot remove the now-stale
    # entry under .git/worktrees/, so it is never treated as a substitute
    # cleanup success. $WorktreeCreated only becomes true after the
    # `git worktree add` above actually succeeded, so a failure to CREATE
    # the worktree never attempts a cleanup of something that was never
    # created.
    if ($WorktreeCreated) {
        Write-Step "Removing isolated release worktree"
        # Scoped ErrorActionPreference: under the script-wide "Stop" setting,
        # a native command's redirected stderr (2>&1) is promoted to a
        # terminating exception the instant git writes anything to stderr --
        # which is exactly what a genuine removal failure does. That would
        # skip the manual fallback and the $CleanupFailed handling below
        # entirely. Restored immediately after, before any other statement.
        $prevErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        & git -C $RepoRoot worktree remove --force $TempWorktreeDir 2>&1 | Out-Null
        $removeExit = $LASTEXITCODE
        $ErrorActionPreference = $prevErrorActionPreference
        if ($removeExit -ne 0 -or (Test-Path $TempWorktreeDir)) {
            $CleanupFailed = $true
            Write-Host "WARNING: 'git worktree remove' did not fully clean up -- attempting manual directory removal." -ForegroundColor Yellow
            try {
                if (Test-Path $TempWorktreeDir) {
                    Remove-Item -Path $TempWorktreeDir -Recurse -Force -ErrorAction Stop
                }
                Write-Info "Isolated worktree directory removed (manual fallback), but git's own worktree registration for it is still stale -- this invocation still FAILS below."
            } catch {
                Write-Host "WARNING: residual isolated worktree could not be removed: $TempWorktreeDir" -ForegroundColor Yellow
                Write-Host "         ($($_.Exception.Message))" -ForegroundColor Yellow
                Write-Host "         This directory is safe to delete manually; it is a detached, disposable" -ForegroundColor Yellow
                Write-Host "         checkout of $CapturedHead and is never reused by a later invocation." -ForegroundColor Yellow
            }
        } else {
            Write-Info "Isolated worktree removed."
        }
    }
}

# ---------------------------------------------------------------------------
# BUG-005-CLEANUP-001: a release invocation is not a certified success just
# because packaging (the try block above) finished -- cleanup of the owned
# worktree must also have actually succeeded. This point in the script is
# reached only when the try block ran to completion without calling Fail()
# (which exits immediately, so it never falls through to here). Do not run
# `git worktree prune` here or anywhere else to paper over a stale
# registration -- that is left for manual/independent resolution.
# ---------------------------------------------------------------------------

if ($CleanupFailed) {
    Fail "Release worktree cleanup did not complete: 'git worktree remove' failed for $TempWorktreeDir, so a stale git worktree registration may remain even though the directory itself may have been removed. The release ZIP/checksum may already exist on disk, but this invocation is NOT a certified success -- resolve $TempWorktreeDir manually (do not run 'git worktree prune')."
}

Write-Step "Release package complete"
Write-Info "ZIP     : $ZipPath"
Write-Info "SHA-256 : $Sha256Path"
Write-Host ""
