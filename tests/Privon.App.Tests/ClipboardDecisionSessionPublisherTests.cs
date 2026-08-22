using System.Reflection;
using Privon.App;
using Privon.Core;
using Privon.Detection;

namespace Privon.App.Tests;

// Phase 3B STEP21 -- ClipboardDecisionSessionPublisher regression. No fakes for the lifecycle --
// this is the real ClipboardDecisionScopeLifecycle (the only production implementation of both
// narrow interfaces), matching ClipboardDecisionScopeLifecycleTests' own "no fakes" precedent for
// the atomicity contract itself. Proves PUBLISH_FLOW/EXPECTED_GENERATION/STALE_PUBLISH_POLICY
// (Phase 3B STEP20 audit, frozen) end to end through the real publisher.
public class ClipboardDecisionSessionPublisherTests
{
    private static readonly ClipboardDecisionItem Item1 =
        new(new CanonicalValue(PiiType.Phone, "01012345678"), RiskLevel.Level1);
    private static readonly ClipboardDecisionItem Item2 =
        new(new CanonicalValue(PiiType.Email, "user@example.com"), RiskLevel.Level3);

    private static ClipboardDecisionPlan SamplePlan(params ClipboardDecisionItem[] items) => new(items);

    // ==================================================================
    // PUBLISHER FLOW (section AA)
    // ==================================================================

    // ---- AA.1. matching generation -> true, scope becomes active ----
    [Fact]
    public void TryPublish_MatchingGeneration_ReturnsTrue_ScopeBecomesActive()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);

        var published = publisher.TryPublish(g1, "raw text", SamplePlan(Item1));

        Assert.True(published);
        Assert.True(lifecycle.HasActiveScope);
    }

    // ---- AA.2. stale generation -> false, no scope published ----
    [Fact]
    public void TryPublish_StaleGeneration_ReturnsFalse_NoScopePublished()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        lifecycle.AdvanceOnClipboardNotification(); // now at g2
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);

        var published = publisher.TryPublish(g1, "raw text", SamplePlan(Item1));

        Assert.False(published);
        Assert.False(lifecycle.HasActiveScope);
    }

    // ---- AA.3. publication uses the EXACT expectedGeneration argument -- never a lazily re-read
    // CurrentGeneration (proven by publishing against the CURRENT generation succeeding, which
    // would be indistinguishable from a bug only if the publisher secretly always used
    // CurrentGeneration -- the stale-rejection test above already rules that reading out) ----
    [Fact]
    public void TryPublish_UsesExactExpectedGenerationArgument()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        lifecycle.AdvanceOnClipboardNotification(); // g1, immediately superseded below
        var g2 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);

        Assert.True(publisher.TryPublish(g2, "raw text", SamplePlan(Item1)));
    }

    // ---- AA.4. one DecisionPlan -> one scope, carrying exactly that plan's Items ----
    [Fact]
    public void TryPublish_OneDecisionPlan_ProducesOneScopeWithExactItems()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var plan = SamplePlan(Item1);

        Assert.True(publisher.TryPublish(g1, "raw text", plan));

        var active = lifecycle.GetActiveScope();
        Assert.NotNull(active);
        Assert.Equal(plan.Items, active!.Items);
    }

    // ---- AA.5. multi-item plan -> ONE scope containing ALL items -- never one scope per item ----
    [Fact]
    public void TryPublish_MultiItemPlan_OneScopeContainsAllItems()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var plan = SamplePlan(Item1, Item2);

        Assert.True(publisher.TryPublish(g1, "raw text", plan));

        var active = lifecycle.GetActiveScope();
        Assert.NotNull(active);
        Assert.Equal(2, active!.Items.Count);
        Assert.Contains(Item1, active.Items);
        Assert.Contains(Item2, active.Items);
    }

    // ---- AA.5.1. (Phase 3C STEP34) the published scope's own Generation equals the EXACT
    // expectedGeneration argument supplied to TryPublish -- never a separately re-read value ----
    [Fact]
    public void TryPublish_ActiveScope_ExposesExactExpectedGeneration()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        lifecycle.AdvanceOnClipboardNotification(); // g1, immediately superseded below
        var g2 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);

        Assert.True(publisher.TryPublish(g2, "raw text", SamplePlan(Item1)));

        var active = lifecycle.GetActiveScope();
        Assert.NotNull(active);
        Assert.Equal(g2, active!.Generation);
    }

    // ---- AA.5.2. sequential publications at different generations produce scopes that each
    // retain their OWN immutable Generation value -- never shared, never rebound ----
    [Fact]
    public void SequentialPublications_EachScopeRetainsItsOwnGeneration()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);

        var g1 = lifecycle.AdvanceOnClipboardNotification();
        Assert.True(publisher.TryPublish(g1, "raw text A", SamplePlan(Item1)));
        var firstScope = lifecycle.GetActiveScope();
        Assert.NotNull(firstScope);
        Assert.Equal(g1, firstScope!.Generation);

        var g2 = lifecycle.AdvanceOnClipboardNotification(); // supersedes firstScope
        Assert.True(publisher.TryPublish(g2, "raw text B", SamplePlan(Item2)));
        var secondScope = lifecycle.GetActiveScope();
        Assert.NotNull(secondScope);
        Assert.Equal(g2, secondScope!.Generation);

        // The first scope's own Generation is untouched by the later publication -- it is a
        // detached object now, not rebound to g2.
        Assert.Equal(g1, firstScope.Generation);
        Assert.NotEqual(firstScope.Generation, secondScope.Generation);
    }

    // ---- AA.5.3. a rejected (stale-generation) publish never mutates/rebinds an already-active
    // scope's own Generation -- the active scope, and its Generation, are left completely alone ----
    [Fact]
    public void RejectedPublish_DoesNotMutateExistingActiveScopeGeneration()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);

        var g1 = lifecycle.AdvanceOnClipboardNotification();
        Assert.True(publisher.TryPublish(g1, "raw text A", SamplePlan(Item1)));
        var active = lifecycle.GetActiveScope();
        Assert.NotNull(active);

        // A stale attempt using an old, already-superseded generation value.
        Assert.False(publisher.TryPublish(g1 - 1, "raw text B", SamplePlan(Item2)));

        var stillActive = lifecycle.GetActiveScope();
        Assert.Same(active, stillActive);
        Assert.Equal(g1, stillActive!.Generation);
    }

    // ---- AA.6. raw text not retained -- structural: neither the publisher nor the resulting
    // scope has any string field ----
    [Fact]
    public void RawText_NotRetained_NoStringFieldOnPublisherOrScope()
    {
        var publisherFields = typeof(ClipboardDecisionSessionPublisher)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var scopeFields = typeof(ClipboardDecisionScope)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(publisherFields, f => f.FieldType == typeof(string));
        Assert.DoesNotContain(scopeFields, f => f.FieldType == typeof(string));
    }

    // ---- AA.7. the publisher's only persistent field is its narrow lifecycle dependency ----
    [Fact]
    public void Publisher_HasOnlyLifecycleDependencyField()
    {
        var fields = typeof(ClipboardDecisionSessionPublisher)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        var forbiddenTypes = new[]
        {
            typeof(string), typeof(ClipboardDecisionPlan), typeof(ClipboardDecisionScope),
            typeof(RevisionTracker), typeof(RevisionStamp), typeof(CanonicalValue),
        };

        Assert.DoesNotContain(fields, f => forbiddenTypes.Contains(f.FieldType));
        Assert.Contains(fields, f => f.FieldType == typeof(IClipboardDecisionScopeLifecycle));
    }

    // ---- AA.8. the DecisionPlan object itself is never retained in the published scope -- only
    // its already-materialized Items ----
    [Fact]
    public void DecisionPlanObjectItself_NotRetainedInScope()
    {
        var scopeFields = typeof(ClipboardDecisionScope)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(scopeFields, f => f.FieldType == typeof(ClipboardDecisionPlan));
    }

    // ---- AA.9. no retry after a stale publish -- a second identical attempt against the same
    // (still stale) generation fails again, and never "catches up" ----
    [Fact]
    public void TryPublish_StaleGeneration_NoImplicitRetry_SecondAttemptStillFails()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);

        Assert.False(publisher.TryPublish(g1, "raw text", SamplePlan(Item1)));
        Assert.False(publisher.TryPublish(g1, "raw text", SamplePlan(Item1)));
        Assert.False(lifecycle.HasActiveScope);
    }

    // ---- AA.10. safe diagnostics -- a real raw-text sentinel never surfaces through the
    // published scope's own diagnostics ----
    [Fact]
    public void RealSentinelRawText_NeverAppearsInPublishedScopeDiagnostics()
    {
        const string sentinel = "RAW-PUBLISHER-SENTINEL-582931";
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);

        Assert.True(publisher.TryPublish(g1, sentinel, SamplePlan(Item1)));

        var active = lifecycle.GetActiveScope();
        Assert.NotNull(active);
        Assert.DoesNotContain(sentinel, active!.ToString());
        Assert.DoesNotContain(sentinel, $"{active}");
    }

    // ---- EXCEPTION_POLICY: unexpected argument failures are never caught inside TryPublish --
    // they propagate to the caller (the coordinator's own outer per-attempt boundary is what keeps
    // the worker loop alive; this type adds no redundant catch of its own) ----
    [Fact]
    public void TryPublish_NullCurrentRawText_Throws()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);

        Assert.Throws<ArgumentNullException>(() => publisher.TryPublish(1, null!, SamplePlan(Item1)));
    }

    [Fact]
    public void TryPublish_NullDecisionPlan_Throws()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);

        Assert.Throws<ArgumentNullException>(() => publisher.TryPublish(1, "raw text", null!));
    }

    [Fact]
    public void Constructor_NullLifecycle_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ClipboardDecisionSessionPublisher(null!));
    }

    // ==================================================================
    // SAME-LIFECYCLE INTEGRATION (section AC)
    // ==================================================================

    // ---- narrow-interface split proof: the SAME concrete ClipboardDecisionScopeLifecycle
    // instance, accessed only through its two separate narrow views
    // (IClipboardNotificationLifecycle for arrival/advance -- exactly what
    // ClipboardPrivacyCoordinator is given; IClipboardDecisionScopeLifecycle for the publisher),
    // deterministically rejects a publish attempt whose captured generation a later arrival has
    // already superseded. No arbitrary sleeps/threads needed -- MODEL A's single lock makes this a
    // plain, deterministic sequence of calls, exactly like
    // ClipboardDecisionScopeLifecycleTests.AdvanceBeforePublish_PublicationRejected already proves
    // at the lifecycle level alone; this test additionally proves it holds through the publisher's
    // own hashing/session-construction work sitting in between. ----
    [Fact]
    public void SameLifecycleInstance_NotificationArrival_SupersedesInFlightPublishAttempt()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        IClipboardNotificationLifecycle notificationView = lifecycle;
        IClipboardDecisionScopeLifecycle decisionScopeView = lifecycle;
        var publisher = new ClipboardDecisionSessionPublisher(decisionScopeView);

        // N1 arrives -- coordinator-side generation capture (OnClipboardChanged's own advance
        // call, via the narrow notification view only).
        var g1 = notificationView.AdvanceOnClipboardNotification();

        // N1's own decision-proposal processing (guarded read/Detection/policy work, in the real
        // flow) would happen at this point -- simulated here simply by not having published g1
        // yet when N2 arrives below.

        // N2 arrives BEFORE N1's publish attempt -- supersedes the in-flight proposal's captured
        // generation via the SAME underlying lifecycle instance, observed through the SAME
        // notification view a real coordinator would use.
        notificationView.AdvanceOnClipboardNotification();

        // N1's publish attempt, using its own captured (now stale) generation, through the
        // publisher's own decision-scope view.
        var published = publisher.TryPublish(g1, "raw text", SamplePlan(Item1));

        Assert.False(published);
        Assert.False(decisionScopeView.HasActiveScope);
    }

    // ==================================================================
    // SCOPE_PUBLISHED (Phase 3C STEP40, section 23)
    // ==================================================================

    // ---- successful publication -> exactly one ScopePublished notification ----
    [Fact]
    public void TryPublish_Success_RaisesScopePublishedExactlyOnce()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var raisedCount = 0;
        publisher.ScopePublished += (_, _) => raisedCount++;

        Assert.True(publisher.TryPublish(g1, "raw text", SamplePlan(Item1)));

        Assert.Equal(1, raisedCount);
    }

    // ---- stale/failed publication -> zero notification ----
    [Fact]
    public void TryPublish_StaleGeneration_NeverRaisesScopePublished()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var raisedCount = 0;
        publisher.ScopePublished += (_, _) => raisedCount++;

        Assert.False(publisher.TryPublish(g1, "raw text", SamplePlan(Item1)));

        Assert.Equal(0, raisedCount);
    }

    // ---- notification carries the exact successfully-published scope reference ----
    [Fact]
    public void TryPublish_Success_NotificationCarriesExactPublishedScopeReference()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        ClipboardDecisionScope? received = null;
        publisher.ScopePublished += (_, scope) => received = scope;

        Assert.True(publisher.TryPublish(g1, "raw text", SamplePlan(Item1)));

        Assert.NotNull(received);
        Assert.Same(lifecycle.GetActiveScope(), received);
    }

    // ---- subscriber runs only after the underlying lifecycle publish has already returned true
    // -- proven by the subscriber observing HasActiveScope already true at the moment it runs ----
    [Fact]
    public void ScopePublished_SubscriberRuns_OnlyAfterLifecyclePublishAlreadySucceeded()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        bool? activeAtNotificationTime = null;
        publisher.ScopePublished += (_, _) => activeAtNotificationTime = lifecycle.HasActiveScope;

        Assert.True(publisher.TryPublish(g1, "raw text", SamplePlan(Item1)));

        Assert.True(activeAtNotificationTime);
    }

    // ---- no raw text / CanonicalValue copied into the event payload -- the payload IS the
    // already-hardened ClipboardDecisionScope reference, nothing additional ----
    [Fact]
    public void ScopePublished_RealSentinelRawText_NeverAppearsInNotificationDiagnostics()
    {
        const string sentinel = "RAW-SCOPEPUBLISHED-SENTINEL-317460";
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        ClipboardDecisionScope? received = null;
        publisher.ScopePublished += (_, scope) => received = scope;

        Assert.True(publisher.TryPublish(g1, sentinel, SamplePlan(Item1)));

        Assert.NotNull(received);
        Assert.DoesNotContain(sentinel, $"{received}");
    }

    // ---- EVENT_FAILURE_CONTAINMENT: a throwing subscriber must never propagate out of TryPublish,
    // and must never affect the true/scope-published outcome of the publication itself ----
    [Fact]
    public void ScopePublished_ThrowingSubscriber_NeverPropagates_PublicationStillSucceeds()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        publisher.ScopePublished += (_, _) => throw new InvalidOperationException("synthetic subscriber failure");

        var published = publisher.TryPublish(g1, "raw text", SamplePlan(Item1));

        Assert.True(published);
        Assert.True(lifecycle.HasActiveScope);
    }

    // ==================================================================
    // ACCESSIBILITY
    // ==================================================================

    [Theory]
    [InlineData(typeof(IClipboardDecisionSessionPublisher))]
    [InlineData(typeof(ClipboardDecisionSessionPublisher))]
    public void PublisherTypes_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }
}
