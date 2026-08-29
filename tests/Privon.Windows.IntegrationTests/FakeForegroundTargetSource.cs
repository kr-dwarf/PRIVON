using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Deterministic, OS-free double for IForegroundTargetSource -- lets ForegroundTargetInspector's
// capture/failure-ordering logic (and, as of Phase 3A.5 STEP4, ClipboardChangeMonitor's
// FOREGROUND_EXECUTION_GUARD) be exercised without any real foreground window or process.
// Defaults to a fully successful resolution so tests only need to override the specific step
// they want to fail.
internal sealed class FakeForegroundTargetSource : IForegroundTargetSource
{
    public nint ForegroundWindowResult { get; set; } = 1;

    public bool WindowThreadProcessIdResult { get; set; } = true;
    public uint WindowThreadProcessIdValue { get; set; } = 4242;

    public bool ProcessNameResult { get; set; } = true;
    public string? ProcessNameValue { get; set; } = "ChatGPT";

    public List<string> CallLog { get; } = [];

    // CHECK1_VS_CHECK2 test seam (Phase 3A.5 STEP4): a "capture attempt" is one full
    // GetForegroundWindow -> TryGetWindowThreadProcessId -> TryGetProcessName sequence (exactly
    // what one CheckForegroundTarget call performs). CaptureAttemptCount increments once per
    // attempt, counted from GetForegroundWindow. When ChangeAfterAttempt is set, every attempt
    // AFTER that count uses the "*After" values instead of the defaults above -- letting a test
    // simulate CHECK 1 (attempt 1) matching and CHECK 2 (attempt 2) then failing, entirely
    // deterministically, with no real timing/threading involved. Unset (null, the default) means
    // every attempt always uses the values above -- every existing test that predates this seam
    // is unaffected.
    public int? ChangeAfterAttempt { get; set; }
    public nint ForegroundWindowResultAfter { get; set; } = 1;
    public bool WindowThreadProcessIdResultAfter { get; set; } = true;
    public uint WindowThreadProcessIdValueAfter { get; set; } = 4242;
    public bool ProcessNameResultAfter { get; set; } = true;
    public string? ProcessNameValueAfter { get; set; } = "ChatGPT";

    public int CaptureAttemptCount { get; private set; }

    private bool UseAfterValues => ChangeAfterAttempt.HasValue && CaptureAttemptCount > ChangeAfterAttempt.Value;

    public nint GetForegroundWindow()
    {
        CallLog.Add(nameof(GetForegroundWindow));
        CaptureAttemptCount++;
        return UseAfterValues ? ForegroundWindowResultAfter : ForegroundWindowResult;
    }

    public bool TryGetWindowThreadProcessId(nint hwnd, out uint processId)
    {
        CallLog.Add(nameof(TryGetWindowThreadProcessId));
        bool result = UseAfterValues ? WindowThreadProcessIdResultAfter : WindowThreadProcessIdResult;
        processId = result ? (UseAfterValues ? WindowThreadProcessIdValueAfter : WindowThreadProcessIdValue) : 0;
        return result;
    }

    /// <summary>
    /// BUG-004 Gate 2H.3 -- when set, the FOREGROUND RE-CONFIRMATION step reports this PID instead
    /// of the pinned one, simulating "the foreground moved to a different process before the
    /// coherent capture could be accepted" (RED-ID-009). Null (the default) means the confirmation
    /// observes the same pinned PID, so every pre-existing test is unaffected.
    /// </summary>
    public uint? ConfirmationForegroundProcessIdOverride { get; set; }

    /// <summary>
    /// BUG-004 Gate 2H.3 -- when false, the confirmation step cannot resolve a foreground process at
    /// all (no foreground window, or its PID could not be resolved). Distinct from a PID mismatch.
    /// </summary>
    public bool ConfirmationForegroundResolvable { get; set; } = true;

    public int ConfirmedIdentityCallCount { get; private set; }

    // BUG-004 Gate 2F -- package-identity fields, additive: default to Unresolved/null so every
    // pre-Gate-2F test (which never sets these) sees exactly the same values a default-initialized
    // ForegroundTargetSnapshot already has, and ForegroundTargetInspector.Capture()'s own existing
    // pre-Gate-2F assertions (which never inspect these two new fields) remain completely unaffected.
    public PackageIdentityResolution PackageIdentityValue { get; set; } = PackageIdentityResolution.Unresolved;
    public string? PackageFamilyNameValue { get; set; }
    public PackageIdentityResolution PackageIdentityValueAfter { get; set; } = PackageIdentityResolution.Unresolved;
    public string? PackageFamilyNameValueAfter { get; set; }

    /// <summary>
    /// BUG-004 Gate 2H.3 (E+) -- the single coherent identity resolution. Models production's own
    /// contract: ONE synthetic "process instance" backs BOTH the process-name and package-identity
    /// facts (never two independent lookups), and the foreground re-confirmation is applied before
    /// any fact is handed back. Reuses the SAME ProcessNameResult/ProcessNameValue(-After) backing
    /// fields the pre-Gate-2H.3 seam used, so every existing test that only ever configured those
    /// keeps exercising the identical behavior.
    /// </summary>
    public bool TryResolveConfirmedForegroundIdentity(
        uint expectedProcessId, out string? processName, out PackageIdentityResolution packageIdentity, out string? packageFamilyName)
    {
        CallLog.Add(nameof(TryResolveConfirmedForegroundIdentity));
        ConfirmedIdentityCallCount++;

        processName = null;
        packageIdentity = PackageIdentityResolution.Unresolved;
        packageFamilyName = null;

        // Step 3/4 -- opening the handle and deriving both facts from it.
        bool resolved = UseAfterValues ? ProcessNameResultAfter : ProcessNameResult;
        if (!resolved)
            return false;

        // Step 5 -- foreground re-confirmation, while the (synthetic) handle is still pinned.
        if (!ConfirmationForegroundResolvable)
            return false;
        uint confirmedPid = ConfirmationForegroundProcessIdOverride ?? expectedProcessId;
        if (confirmedPid != expectedProcessId)
            return false;

        processName = UseAfterValues ? ProcessNameValueAfter : ProcessNameValue;
        packageIdentity = UseAfterValues ? PackageIdentityValueAfter : PackageIdentityValue;
        packageFamilyName = UseAfterValues ? PackageFamilyNameValueAfter : PackageFamilyNameValue;
        return true;
    }
}
