using System.Reflection;
using Privon.App;
using Privon.Core;
using Privon.Detection;

namespace Privon.App.Tests;

// Phase 3B STEP21 -- ClipboardDecisionScope payload regression. Proves the exact contract the
// Phase 3B STEP18/STEP20 audits froze: a private, per-session RevisionTracker; an immutable
// InitialStamp captured once at construction from the exact raw text that surfaced the decision;
// immutable Items; and ObserveCurrent as the ONLY way to use the private tracker (no
// RevisionTracker/ForceAdvance ever reachable through this type). No fakes -- exercises the real
// Privon.Core.RevisionTracker directly, exactly like ClipboardDecisionSessionPublisher will.
public class ClipboardDecisionScopeTests
{
    private static readonly ClipboardDecisionItem SampleItem =
        new(new CanonicalValue(PiiType.Phone, "01012345678"), RiskLevel.Level1);

    // ---- Z.1. constructor retains InitialStamp exactly as produced by the tracker's own Observe
    // call ----
    [Fact]
    public void Constructor_RetainsInitialStamp()
    {
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe("raw-text-A");

        var scope = new ClipboardDecisionScope(tracker, stamp, [], generation: 0);

        Assert.Equal(stamp, scope.InitialStamp);
    }

    // ---- Z.2. Items is exposed exactly as given, with no raw text anywhere in the identity it
    // carries (CanonicalValue itself is already diagnostic-hardened -- Phase 3B STEP3.1 -- this
    // just confirms the scope does not add a second, unhardened path to the same content) ----
    [Fact]
    public void Items_ExposedWithoutRawText()
    {
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe("raw-text-A");
        var items = new[] { SampleItem };

        var scope = new ClipboardDecisionScope(tracker, stamp, items, generation: 0);

        Assert.Equal(items, scope.Items);
    }

    // ---- Z.4. ObserveCurrent(same text) -> Matches InitialStamp ----
    [Fact]
    public void ObserveCurrent_SameText_MatchesInitialStamp()
    {
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe("raw-text-A");
        var scope = new ClipboardDecisionScope(tracker, stamp, [], generation: 0);

        var current = scope.ObserveCurrent("raw-text-A");

        Assert.True(current.Matches(scope.InitialStamp));
    }

    // ---- Z.5. ObserveCurrent(changed text) -> does not Match ----
    [Fact]
    public void ObserveCurrent_ChangedText_DoesNotMatch()
    {
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe("raw-text-A");
        var scope = new ClipboardDecisionScope(tracker, stamp, [], generation: 0);

        var current = scope.ObserveCurrent("raw-text-B");

        Assert.False(current.Matches(scope.InitialStamp));
    }

    // ---- Z.6. A -> B -> A: the final A does NOT match InitialStamp, even though its
    // SnapshotHash is identical to the original A -- the RevisionId strictly advanced across the
    // B detour, and RevisionStamp.Matches requires both fields (INITIAL_STAMP_CONTRACT) ----
    [Fact]
    public void ObserveCurrent_ABA_FinalADoesNotMatchInitialStamp()
    {
        var tracker = new RevisionTracker();
        var initialStamp = tracker.Observe("raw-text-A");
        var scope = new ClipboardDecisionScope(tracker, initialStamp, [], generation: 0);

        scope.ObserveCurrent("raw-text-B");
        var finalA = scope.ObserveCurrent("raw-text-A");

        Assert.False(finalA.Matches(scope.InitialStamp));
        Assert.NotEqual(scope.InitialStamp.RevisionId, finalA.RevisionId);
    }

    // ---- Z.7. REVISIONTRACKER_ENCAPSULATION: no property or field of this type exposes the
    // private RevisionTracker itself, at ANY accessibility level ----
    [Fact]
    public void Scope_ExposesNoRevisionTrackerMember()
    {
        var members = typeof(ClipboardDecisionScope)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(members, m =>
            (m is PropertyInfo p && p.PropertyType == typeof(RevisionTracker)) ||
            (m is MethodInfo mi && mi.ReturnType == typeof(RevisionTracker)));
    }

    // ---- Z.8. no ForceAdvance exposure of any kind on the scope's own public/internal surface ----
    [Fact]
    public void Scope_HasNoForceAdvanceMember()
    {
        var members = typeof(ClipboardDecisionScope)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name => name.Contains("ForceAdvance", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Z.9. SCOPE_DIAGNOSTICS: safe ToString -- reports only ItemCount, the InitialStamp's own
    // safe RevisionId, and (Phase 3C STEP34) Generation itself (already-safe metadata, same
    // classification as RevisionId) -- never CanonicalValue content, SnapshotHash, raw text, an
    // item enumeration, or any fingerprint ----
    [Fact]
    public void ToString_ExposesOnlySafeMetadata_NoSentinelOrHash()
    {
        const string sentinel = "RAW-DECISION-SCOPE-SENTINEL-361042";
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe(sentinel);
        var canonical = new CanonicalValue(PiiType.Phone, sentinel);
        var scope = new ClipboardDecisionScope(tracker, stamp, [new ClipboardDecisionItem(canonical, RiskLevel.Level1)], generation: 7);

        var text = scope.ToString();

        Assert.DoesNotContain(sentinel, text);
        // Never even touches Hash's own rendering (self-contained hardening -- see class doc) --
        // "SnapshotHash" would only appear here if this type's ToString ever delegated to
        // RevisionStamp.Hash in any form.
        Assert.DoesNotContain("SnapshotHash", text);
        Assert.Contains("ItemCount", text);
        Assert.Contains("InitialRevisionId", text);
        Assert.Contains("Generation = 7", text);
    }

    // ---- Z.9.1. interpolation (not just direct ToString() call) is equally safe ----
    [Fact]
    public void Interpolation_DoesNotContainRawSentinel()
    {
        const string sentinel = "RAW-DECISION-SCOPE-SENTINEL-361042";
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe(sentinel);
        var scope = new ClipboardDecisionScope(tracker, stamp, [], generation: 0);

        Assert.DoesNotContain(sentinel, $"{scope}");
    }

    // ---- Z.10. no DebuggerDisplay/DebuggerTypeProxy leak ----
    [Fact]
    public void Scope_HasNoDebuggerDisplayOrTypeProxyAttributes()
    {
        var attributes = typeof(ClipboardDecisionScope).GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }

    // ==================================================================
    // Z.11+. GENERATION (Phase 3C STEP34)
    // ==================================================================

    // ---- Z.11. Generation equals exactly the constructor value supplied ----
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(42L)]
    public void Generation_EqualsExactConstructorValue(long generation)
    {
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe("raw-text-A");

        var scope = new ClipboardDecisionScope(tracker, stamp, [], generation);

        Assert.Equal(generation, scope.Generation);
    }

    // ---- Z.12. Generation is immutable -- no setter of any kind on the property ----
    [Fact]
    public void Generation_HasNoSetter()
    {
        var property = typeof(ClipboardDecisionScope).GetProperty(nameof(ClipboardDecisionScope.Generation));

        Assert.NotNull(property);
        Assert.Null(property!.SetMethod);
    }

    // ---- Z.13. two scopes built from the same tracker/items but different Generation values
    // retain their OWN independent values -- Generation is never shared/aliased/recomputed from
    // anything else the scope carries ----
    [Fact]
    public void DifferentScopes_RetainIndependentGenerationValues()
    {
        var trackerA = new RevisionTracker();
        var stampA = trackerA.Observe("raw-text-A");
        var trackerB = new RevisionTracker();
        var stampB = trackerB.Observe("raw-text-A"); // same text, different tracker instance

        var scopeAtGenerationOne = new ClipboardDecisionScope(trackerA, stampA, [], generation: 1);
        var scopeAtGenerationFive = new ClipboardDecisionScope(trackerB, stampB, [], generation: 5);

        Assert.Equal(1, scopeAtGenerationOne.Generation);
        Assert.Equal(5, scopeAtGenerationFive.Generation);
    }

    // ---- Z.14. reference-based identity/equality semantics are unaffected by adding Generation --
    // two scopes with the SAME Generation value are still never treated as interchangeable (no
    // value-based Equals/GetHashCode override was introduced) ----
    [Fact]
    public void Scope_RemainsReferenceIdentity_EvenWithEqualGeneration()
    {
        var trackerA = new RevisionTracker();
        var stampA = trackerA.Observe("raw-text-A");
        var trackerB = new RevisionTracker();
        var stampB = trackerB.Observe("raw-text-A");

        var scopeOne = new ClipboardDecisionScope(trackerA, stampA, [], generation: 3);
        var scopeTwo = new ClipboardDecisionScope(trackerB, stampB, [], generation: 3);

        Assert.False(ReferenceEquals(scopeOne, scopeTwo));
        Assert.NotEqual(scopeOne, scopeTwo); // plain sealed class -- no record-style value equality
    }

    // ---- Z.15. resolution-state operations (ApplyIntent/CommitResolved/grants) are unaffected by
    // a non-default Generation -- no regression introduced by the new field ----
    [Fact]
    public void ResolutionStateOperations_UnaffectedByNonDefaultGeneration()
    {
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe("raw-text-A");
        var item = new ClipboardDecisionItem(new CanonicalValue(PiiType.Phone, "01012345678"), RiskLevel.Level1);
        var scope = new ClipboardDecisionScope(tracker, stamp, [item], generation: 99);

        var choice = scope.ApplyIntent(item, ClipboardDecisionIntent.BypassOnce);
        Assert.Equal(ClipboardRuntimeItemChoice.BypassOnce, choice);
        Assert.True(scope.AllItemsResolved);

        scope.CommitResolved(scope.InitialStamp);

        Assert.True(scope.Committed);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.Canonical));
        Assert.Equal(99, scope.Generation); // untouched by resolution-state mutation
    }
}
