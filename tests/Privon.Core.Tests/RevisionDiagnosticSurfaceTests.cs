using System.Security.Cryptography;
using System.Text;
using Privon.Core;

namespace Privon.Core.Tests;

// Phase 3B STEP15.1 -- SnapshotHash/RevisionStamp diagnostic-surface hardening regression.
// STEP15's own audit found that SnapshotHash.ToString() exposed the raw SHA-256 hex digest
// directly (and RevisionStamp's compiler-synthesized ToString transitively leaked it too) --
// this file proves that debt is closed, without touching From()/equality/RevisionTracker
// behavior.
public class RevisionDiagnosticSurfaceTests
{
    private const string Sentinel = "RAW-CORE-REVISION-SENTINEL-518203";

    // Computed independently of SnapshotHash.ToString() (which is exactly what's being
    // hardened) using the same algorithm SnapshotHash.From documents (SHA-256 over UTF-8 bytes,
    // uppercase hex) -- this is the exact digest string that must never appear anywhere.
    private static string ComputeExpectedHexDigest(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest);
    }

    // ==================================================================
    // SNAPSHOTHASH_DIAGNOSTIC_SURFACE
    // ==================================================================

    // ---- 1. SnapshotHash.ToString() does not contain the expected SHA-256 hex digest ----
    [Fact]
    public void SnapshotHash_ToString_DoesNotContainHexDigest()
    {
        var hash = SnapshotHash.From(Sentinel);
        var expectedDigest = ComputeExpectedHexDigest(Sentinel);

        Assert.DoesNotContain(expectedDigest, hash.ToString());
    }

    // ---- 2. SnapshotHash interpolation does not contain the digest ----
    [Fact]
    public void SnapshotHash_Interpolation_DoesNotContainHexDigest()
    {
        var hash = SnapshotHash.From(Sentinel);
        var expectedDigest = ComputeExpectedHexDigest(Sentinel);

        Assert.DoesNotContain(expectedDigest, $"{hash}");
    }

    // ---- 3. SnapshotHash.ToString() contains no raw sentinel either ----
    [Fact]
    public void SnapshotHash_ToString_DoesNotContainRawSentinel()
    {
        var hash = SnapshotHash.From(Sentinel);

        Assert.DoesNotContain(Sentinel, hash.ToString());
        Assert.DoesNotContain(Sentinel, $"{hash}");
    }

    // ==================================================================
    // REVISIONSTAMP_DIAGNOSTIC_SURFACE
    // ==================================================================

    // ---- 4. RevisionStamp.ToString() does not contain the SnapshotHash digest ----
    [Fact]
    public void RevisionStamp_ToString_DoesNotContainSnapshotHashDigest()
    {
        var stamp = new RevisionStamp(new RevisionId(1), SnapshotHash.From(Sentinel));
        var expectedDigest = ComputeExpectedHexDigest(Sentinel);

        Assert.DoesNotContain(expectedDigest, stamp.ToString());
    }

    // ---- 5. RevisionStamp interpolation does not contain the digest ----
    [Fact]
    public void RevisionStamp_Interpolation_DoesNotContainSnapshotHashDigest()
    {
        var stamp = new RevisionStamp(new RevisionId(1), SnapshotHash.From(Sentinel));
        var expectedDigest = ComputeExpectedHexDigest(Sentinel);

        Assert.DoesNotContain(expectedDigest, $"{stamp}");
    }

    // ---- 6. RevisionStamp.ToString() contains no raw sentinel either ----
    [Fact]
    public void RevisionStamp_ToString_DoesNotContainRawSentinel()
    {
        var stamp = new RevisionStamp(new RevisionId(1), SnapshotHash.From(Sentinel));

        Assert.DoesNotContain(Sentinel, stamp.ToString());
        Assert.DoesNotContain(Sentinel, $"{stamp}");
    }

    // ---- 7. RevisionStamp's safe diagnostic retains RevisionId (already-safe metadata) ----
    [Fact]
    public void RevisionStamp_ToString_ContainsRevisionId()
    {
        var stamp = new RevisionStamp(new RevisionId(42), SnapshotHash.From(Sentinel));

        Assert.Contains("42", stamp.ToString());
        Assert.Contains("RevisionId", stamp.ToString());
    }

    // ==================================================================
    // BEHAVIOR_PRESERVATION -- diagnostic hardening changes ToString only; algorithm,
    // equality, and RevisionTracker semantics are unchanged (regression net alongside the
    // existing RevisionTrackerTests.cs suite, which is also re-run unmodified).
    // ==================================================================

    // ---- 8. same exact raw state -> same SnapshotHash ----
    [Fact]
    public void SnapshotHash_SameExactText_ProducesEqualHash()
    {
        var first = SnapshotHash.From(Sentinel);
        var second = SnapshotHash.From(Sentinel);

        Assert.Equal(first, second);
    }

    // ---- 9. different raw states -> different SnapshotHash ----
    [Fact]
    public void SnapshotHash_DifferentText_ProducesDifferentHash()
    {
        var first = SnapshotHash.From(Sentinel);
        var second = SnapshotHash.From(Sentinel + "-changed");

        Assert.NotEqual(first, second);
    }

    // ---- 10. RevisionStamp.Matches still requires BOTH RevisionId and SnapshotHash equal ----
    [Fact]
    public void RevisionStamp_Matches_RequiresBothRevisionIdAndHash()
    {
        var hash = SnapshotHash.From(Sentinel);
        var baseline = new RevisionStamp(new RevisionId(1), hash);

        var sameIdDifferentHash = new RevisionStamp(new RevisionId(1), SnapshotHash.From(Sentinel + "-x"));
        var differentIdSameHash = new RevisionStamp(new RevisionId(2), hash);
        var exact = new RevisionStamp(new RevisionId(1), hash);

        Assert.False(baseline.Matches(sameIdDifferentHash));
        Assert.False(baseline.Matches(differentIdSameHash));
        Assert.True(baseline.Matches(exact));
    }

    // ---- 11. observing the same text twice still does not advance the revision ----
    [Fact]
    public void RevisionTracker_ObservingSameTextTwice_StillDoesNotAdvance()
    {
        var tracker = new RevisionTracker();
        var first = tracker.Observe(Sentinel);
        var second = tracker.Observe(Sentinel);

        Assert.Equal(first.RevisionId, second.RevisionId);
    }

    // ---- 12. observing changed text still advances the revision ----
    [Fact]
    public void RevisionTracker_ObservingChangedText_StillAdvances()
    {
        var tracker = new RevisionTracker();
        var before = tracker.Observe(Sentinel);
        var after = tracker.Observe(Sentinel + "-changed");

        Assert.NotEqual(before.RevisionId, after.RevisionId);
    }

    // ---- 13. text1 -> text2 -> text1 still produces a LATER RevisionId for the second text1
    // (the exact property STEP15's ALIASMAP_LIFETIME_DIFFERENCE/REVISIONID_SEMANTICS finding
    // depends on -- must survive this hardening unchanged) ----
    [Fact]
    public void RevisionTracker_TextReturnsToOriginal_ProducesLaterRevisionIdThanOriginal()
    {
        var tracker = new RevisionTracker();
        var firstText1 = tracker.Observe(Sentinel);
        tracker.Observe(Sentinel + "-changed");
        var secondText1 = tracker.Observe(Sentinel);

        Assert.Equal(firstText1.Hash, secondText1.Hash);
        Assert.NotEqual(firstText1.RevisionId, secondText1.RevisionId);
        Assert.False(firstText1.Matches(secondText1));
    }

    // ==================================================================
    // DEBUGGER_SURFACE
    // ==================================================================

    [Theory]
    [InlineData(typeof(SnapshotHash))]
    [InlineData(typeof(RevisionStamp))]
    [InlineData(typeof(RevisionId))]
    [InlineData(typeof(RevisionTracker))]
    public void RevisionTypes_HaveNoDebuggerDisplayOrTypeProxyAttributes(Type type)
    {
        var attributes = type.GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }
}
