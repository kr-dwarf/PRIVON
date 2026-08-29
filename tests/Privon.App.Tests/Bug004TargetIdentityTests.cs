using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// BUG-004 Gate 2F -- regression coverage for the CONFIRMED S2 defect this suite was originally
// written to prove and that TargetGate's live package-identity policy now closes: TargetGate used
// to authorize ANY process whose OS-reported ProcessName was "ChatGPT", regardless of Windows
// package identity -- so a same-name process belonging to a completely different application
// (proven, this session, against the real, currently-installed "OpenAI Codex" AND "ChatGPT Classic"
// packages) was indistinguishable from the one product-owner-approved application.
//
// GATE_2E/2E.1-closed product-identity contract (real, locally + Microsoft-Store-catalog verified,
// not guessed):
//   SUPPORTED_CURRENT_PFN = "OpenAI.Codex_2p2nqsd0c76g0"  (the current/latest ChatGPT Desktop app --
//     yes, that IS its real package identity; see Gate 2E.1's own triangulated evidence)
//   UNSUPPORTED           = "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0" (ChatGPT Classic, real negative
//     control, now actually installed on this machine), every other PFN, no package identity at
//     all, and every unresolved-identity case.
//
// GATE_2F_SCOPE: TargetGate has since been strengthened to the package-identity policy described
// above (see TargetGate.IsSupportedTarget's own SUPPORTED_IDENTITY_0_2_1 doc) -- every RED-004-###
// test below now passes, characterizing live behavior rather than a missing one; the RED-004-###
// naming is kept as historical record of the defect each test was originally written to prove.
public class Bug004TargetIdentityTests
{
    private const string SupportedCurrentPfn = "OpenAI.Codex_2p2nqsd0c76g0";
    private const string ChatGptClassicPfn = "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0";

    private static ForegroundTargetSnapshot Snapshot(
        string? processName,
        PackageIdentityResolution packageIdentity = PackageIdentityResolution.Unresolved,
        string? packageFamilyName = null,
        uint processId = 4242) =>
        new(IsResolved: true, ProcessId: processId, ProcessName: processName,
            PackageIdentity: packageIdentity, PackageFamilyName: packageFamilyName);

    // ---- RED-004-001: the current, product-owner-approved ChatGPT Desktop identity -> SUPPORTED.
    // The positive characterization the live package-identity policy must never break. ----
    [Fact]
    public void Red004001_CurrentChatGptDesktopIdentity_ExpectedFutureSupported()
    {
        var snapshot = Snapshot("ChatGPT", PackageIdentityResolution.Resolved, SupportedCurrentPfn);

        bool supported = TargetGate.IsSupportedTarget(snapshot);

        Assert.True(supported,
            "RED-004-001 (positive characterization): the current supported ChatGPT Desktop identity " +
            "(ProcessName=ChatGPT, PFN=" + SupportedCurrentPfn + ") must be authorized. TargetGate's live " +
            "package-identity policy already returns true here -- this test locks that positive case down " +
            "so a future change can never regress it.");
    }

    // ---- RED-004-002: ChatGPT Classic -- real negative control, now actually installed on this
    // machine (Gate 2E.1). Same process name, different, explicitly product-owner-rejected PFN. ----
    [Fact]
    public void Red004002_ChatGptClassicIdentity_ExpectedFutureUnsupported()
    {
        var snapshot = Snapshot("ChatGPT", PackageIdentityResolution.Resolved, ChatGptClassicPfn);

        bool supported = TargetGate.IsSupportedTarget(snapshot);

        Assert.False(supported,
            "RED-004-002: ChatGPT Classic (PFN=" + ChatGptClassicPfn + ") must NOT be authorized under " +
            "BUG-004's live package-identity policy, even though ProcessName == \"ChatGPT\" matches -- " +
            "package identity, not process name alone, is what determines which real, installed OpenAI " +
            "package that process actually belongs to.");
    }

    // ---- RED-004-003: a different OpenAI package (neither the supported current app nor Classic) ----
    [Fact]
    public void Red004003_OtherOpenAiPackageIdentity_ExpectedFutureUnsupported()
    {
        var snapshot = Snapshot("ChatGPT", PackageIdentityResolution.Resolved, "OpenAI.SomeOtherProduct_2p2nqsd0c76g0");

        bool supported = TargetGate.IsSupportedTarget(snapshot);

        Assert.False(supported,
            "RED-004-003: an unrelated OpenAI-published package presenting ProcessName == \"ChatGPT\" must " +
            "NOT be authorized -- package identity, not process name alone, is TargetGate's authorization " +
            "boundary.");
    }

    // ---- RED-004-004: unpackaged same-name executable -- the exact BUG-004 scenario, no
    // fabricated/deceptive binary needed: a real Windows API result (APPMODEL_ERROR_NO_PACKAGE) is
    // simply represented as data. ----
    [Fact]
    public void Red004004_UnpackagedSameNameExecutable_ExpectedFutureUnsupported()
    {
        var snapshot = Snapshot("ChatGPT", PackageIdentityResolution.NoPackage, packageFamilyName: null);

        bool supported = TargetGate.IsSupportedTarget(snapshot);

        Assert.False(supported,
            "RED-004-004: an ordinary unpackaged executable named ChatGPT.exe (no Windows package " +
            "identity at all) must NOT be authorized -- process name alone is never sufficient under " +
            "TargetGate's package-identity policy.");
    }

    // ---- RED-004-005: package-identity inspection unresolved/inconclusive -- must fail closed,
    // never silently fall back to name-only authorization. ----
    [Fact]
    public void Red004005_UnresolvedPackageIdentityInspection_ExpectedFutureUnsupported()
    {
        var snapshot = Snapshot("ChatGPT", PackageIdentityResolution.Unresolved, packageFamilyName: null);

        bool supported = TargetGate.IsSupportedTarget(snapshot);

        Assert.False(supported,
            "RED-004-005: an inconclusive/failed package-identity inspection must fail closed to " +
            "UNSUPPORTED, never fall back to the process name alone.");
    }

    // ---- RED-004-006: the supported PFN is stable across ordinary version changes -- the fact
    // model structurally carries no version/PackageFullName to vary (Gate 2F FACT-004), so this is
    // expressed as two independently-constructed snapshots sharing the same PFN, each still
    // SUPPORTED, proving the live policy will never need (or be able) to pin a version. Same
    // positive-characterization status as RED-004-001. ----
    [Fact]
    public void Red004006_SupportedPfnAcrossIndependentCaptures_ExpectedFutureSupportedBothTimes()
    {
        var beforeUpdate = Snapshot("ChatGPT", PackageIdentityResolution.Resolved, SupportedCurrentPfn, processId: 100);
        var afterUpdate = Snapshot("ChatGPT", PackageIdentityResolution.Resolved, SupportedCurrentPfn, processId: 200);

        Assert.True(TargetGate.IsSupportedTarget(beforeUpdate),
            "RED-004-006: the supported PFN must remain authorized independent of process identity " +
            "churn (e.g. an app restart after an update) -- no version/PackageFullName is ever part of " +
            "this fact model to pin (Gate 2F FACT-004), so nothing here should ever need to change " +
            "across an ordinary official update.");
        Assert.True(TargetGate.IsSupportedTarget(afterUpdate),
            "RED-004-006: same as above, second independently-constructed snapshot.");
    }

    // ---- RED-004-007: wrong process name even with the supported PFN -- the cheap name prefilter
    // must still apply. Should already pass today (name check already rejects this). ----
    [Fact]
    public void Red004007_WrongProcessNameWithSupportedPfn_ExpectedFutureUnsupported()
    {
        var snapshot = Snapshot("notepad", PackageIdentityResolution.Resolved, SupportedCurrentPfn);

        bool supported = TargetGate.IsSupportedTarget(snapshot);

        Assert.False(supported,
            "RED-004-007 (positive characterization): a process named \"notepad\" must never be " +
            "authorized regardless of PFN -- the process-name prefilter already rejects this on the " +
            "process name alone, and the live package-identity policy must never weaken that.");
    }

    // ---- RED-004-008: an unknown/future PackageIdentityResolution state must default to
    // UNSUPPORTED, fail-closed -- TargetGate only ever treats an exact
    // PackageIdentityResolution.Resolved match as eligible; any other value, including one this
    // type has never seen before, falls closed rather than being treated as "close enough". ----
    [Fact]
    public void Red004008_UnknownFuturePackageIdentityState_ExpectedFutureUnsupported()
    {
        var undefinedState = (PackageIdentityResolution)999;
        var snapshot = Snapshot("ChatGPT", undefinedState, packageFamilyName: null);

        bool supported = TargetGate.IsSupportedTarget(snapshot);

        Assert.False(supported,
            "RED-004-008: an undefined/future PackageIdentityResolution value must fail closed to " +
            "UNSUPPORTED -- TargetGate only treats an exact Resolved match as eligible.");
    }
}
