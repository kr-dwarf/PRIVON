using System.Reflection;
using Privon.App;
using Privon.Core;
using Privon.Detection;

namespace Privon.App.Tests;

// Phase 3B STEP25 -- ClipboardDecisionScope runtime resolution-state regression. Proves the exact
// transition tables, readiness/barrier computation, and grant-creation-at-commit-only contract the
// Phase 3B STEP24/STEP24.1 audits froze, plus the single-immutable-snapshot atomicity mechanism
// (ClipboardDecisionResolutionState) those audits selected. No fakes -- exercises the real
// Privon.Core.RevisionTracker and the real production types directly.
public class ClipboardDecisionScopeResolutionTests
{
    private static readonly ClipboardDecisionItem Level1Item =
        new(new CanonicalValue(PiiType.Phone, "01012345678"), RiskLevel.Level1);
    private static readonly ClipboardDecisionItem Level3Item =
        new(new CanonicalValue(PiiType.ResidentRegistrationNumber, "901231-1234567"), RiskLevel.Level3);

    private static ClipboardDecisionScope CreateScope(params ClipboardDecisionItem[] items)
    {
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe("scope-resolution-test-marker");
        return new ClipboardDecisionScope(tracker, stamp, items, generation: 0);
    }

    // ==================================================================
    // AB. TRANSITIONS
    // ==================================================================

    // ---- 1. initial state: no choices/no grants/not committed ----
    [Fact]
    public void InitialState_NoChoicesNoGrantsNotCommitted()
    {
        var scope = CreateScope(Level1Item);

        Assert.Equal(ClipboardRuntimeItemChoice.Unresolved, scope.GetChoice(Level1Item));
        Assert.False(scope.Committed);
        Assert.False(scope.AllItemsResolved);
    }

    // ---- 2. Level1 Protect ----
    [Fact]
    public void Level1_Protect_YieldsFinalProtect()
    {
        var scope = CreateScope(Level1Item);
        var result = scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardRuntimeItemChoice.Protect, result);
        Assert.Equal(ClipboardRuntimeItemChoice.Protect, scope.GetChoice(Level1Item));
    }

    // ---- 3. Level1 BypassOnce ----
    [Fact]
    public void Level1_BypassOnce_YieldsFinalBypassOnce()
    {
        var scope = CreateScope(Level1Item);
        var result = scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardRuntimeItemChoice.BypassOnce, result);
    }

    // ---- 4. Level1 Protect -> BypassOnce ----
    [Fact]
    public void Level1_ProtectThenBypassOnce_OverwritesToBypassOnce()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);
        var result = scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardRuntimeItemChoice.BypassOnce, result);
    }

    // ---- 5. Level1 BypassOnce -> Protect ----
    [Fact]
    public void Level1_BypassOnceThenProtect_OverwritesToProtect()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.BypassOnce);
        var result = scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardRuntimeItemChoice.Protect, result);
    }

    // ---- 6. Level3 Protect -- immediate final, no second confirmation ----
    [Fact]
    public void Level3_Protect_YieldsImmediateFinalProtect_NoConfirmation()
    {
        var scope = CreateScope(Level3Item);
        var result = scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardRuntimeItemChoice.Protect, result);
    }

    // ---- 7. Level3 first BypassOnce -> Awaiting ----
    [Fact]
    public void Level3_FirstBypassOnce_YieldsAwaitingConfirmation_NotFinal()
    {
        var scope = CreateScope(Level3Item);
        var result = scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation, result);
    }

    // ---- 8. Level3 second BypassOnce -> final Bypass ----
    [Fact]
    public void Level3_SecondBypassOnce_FromAwaiting_YieldsFinalBypassOnce()
    {
        var scope = CreateScope(Level3Item);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // first -> Awaiting
        var result = scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // second -> final

        Assert.Equal(ClipboardRuntimeItemChoice.BypassOnce, result);
    }

    // ---- 9. Level3 Awaiting -> Protect ----
    [Fact]
    public void Level3_AwaitingConfirmation_ProtectInstead_YieldsFinalProtect()
    {
        var scope = CreateScope(Level3Item);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // -> Awaiting
        var result = scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardRuntimeItemChoice.Protect, result);
    }

    // ---- 10. Level3 Protect -> BypassOnce -> Awaiting (CRITICAL: never jumps directly to final) ----
    [Fact]
    public void Level3_ProtectThenBypassOnce_RestartsAtAwaiting_NeverFinalDirectly()
    {
        var scope = CreateScope(Level3Item);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.Protect);
        var result = scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation, result);
    }

    // ---- 11. Level3 Protect -> Awaiting -> BypassOnce final ----
    [Fact]
    public void Level3_ProtectThenAwaitingThenBypassOnce_YieldsFinalBypassOnce()
    {
        var scope = CreateScope(Level3Item);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.Protect);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // -> Awaiting
        var result = scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // -> final

        Assert.Equal(ClipboardRuntimeItemChoice.BypassOnce, result);
    }

    // ---- 12. Level3 final BypassOnce -> Protect ----
    [Fact]
    public void Level3_FinalBypassOnce_ProtectInstead_YieldsFinalProtect()
    {
        var scope = CreateScope(Level3Item);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // -> final BypassOnce
        var result = scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardRuntimeItemChoice.Protect, result);
    }

    // ---- 13. Level3 final BypassOnce -> Protect -> BypassOnce -> Awaiting, NOT final Bypass ----
    [Fact]
    public void Level3_FinalBypassOnce_ProtectThenBypassOnce_YieldsAwaiting_NotFinalBypass()
    {
        var scope = CreateScope(Level3Item);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // -> final BypassOnce
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.Protect); // -> final Protect
        var result = scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // -> Awaiting again

        Assert.Equal(ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation, result);
    }

    // ---- 14. same final choice repeated idempotently (Level1 final, and Level3 AT THE FINAL
    // BypassOnce stage specifically -- NOT at the Awaiting stage, which test 8 already proves is
    // NOT idempotent-with-itself in the naive sense) ----
    [Fact]
    public void SameFinalChoiceRepeated_IsIdempotent()
    {
        var level1Scope = CreateScope(Level1Item);
        level1Scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);
        Assert.Equal(ClipboardRuntimeItemChoice.Protect, level1Scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect));

        var level3Scope = CreateScope(Level3Item);
        level3Scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce);
        level3Scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // -> final
        var repeated = level3Scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardRuntimeItemChoice.BypassOnce, repeated);
    }

    // ---- 15. item not belonging to scope rejected ----
    [Fact]
    public void ApplyIntent_ItemNotBelongingToScope_Throws()
    {
        var scope = CreateScope(Level1Item);
        var foreignItem = new ClipboardDecisionItem(new CanonicalValue(PiiType.Email, "user@example.com"), RiskLevel.Level1);

        Assert.Throws<ArgumentException>(() => scope.ApplyIntent(foreignItem, ClipboardDecisionIntent.Protect));
    }

    // ---- 16. mutation after committed rejected ----
    [Fact]
    public void ApplyIntent_AfterCommitted_Throws()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);
        scope.CommitResolved(scope.InitialStamp);

        Assert.Throws<InvalidOperationException>(() => scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.BypassOnce));
    }

    // ---- structural: undefined RiskLevel (Level2 can never legitimately appear in a real
    // DecisionPlan, but the API must still fail closed rather than silently fall through) ----
    [Fact]
    public void ApplyIntent_UnsupportedRiskLevel_Throws()
    {
        var level2Item = new ClipboardDecisionItem(new CanonicalValue(PiiType.Email, "user@example.com"), RiskLevel.Level2);
        var scope = CreateScope(level2Item);

        Assert.Throws<ArgumentOutOfRangeException>(() => scope.ApplyIntent(level2Item, ClipboardDecisionIntent.Protect));
    }

    // ==================================================================
    // AC. READINESS
    // ==================================================================

    // ---- 1. one unresolved item -> not ready ----
    [Fact]
    public void Readiness_UnresolvedItem_NotReady()
    {
        var scope = CreateScope(Level1Item);
        Assert.False(scope.AllItemsResolved);
    }

    // ---- 2. Awaiting Level3 item -> not ready ----
    [Fact]
    public void Readiness_AwaitingLevel3Item_NotReady()
    {
        var scope = CreateScope(Level3Item);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // -> Awaiting

        Assert.False(scope.AllItemsResolved);
    }

    // ---- 3. all final Protect -> ready ----
    [Fact]
    public void Readiness_AllFinalProtect_Ready()
    {
        var scope = CreateScope(Level1Item, Level3Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.Protect);

        Assert.True(scope.AllItemsResolved);
    }

    // ---- 4. all final Bypass -> ready ----
    [Fact]
    public void Readiness_AllFinalBypass_Ready()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.BypassOnce);

        Assert.True(scope.AllItemsResolved);
    }

    // ---- 5. mixed Protect+Bypass -> ready ----
    [Fact]
    public void Readiness_MixedProtectAndBypass_Ready()
    {
        var scope = CreateScope(Level1Item, Level3Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // -> final

        Assert.True(scope.AllItemsResolved);
    }

    // ---- 6. multi-item with one missing -> not ready ----
    [Fact]
    public void Readiness_MultiItemOneMissing_NotReady()
    {
        var scope = CreateScope(Level1Item, Level3Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);
        // Level3Item left Unresolved.

        Assert.False(scope.AllItemsResolved);
    }

    // ==================================================================
    // AD. GRANTS
    // ==================================================================

    // ---- 1. Bypass provisional/final choice before commit -> zero grants ----
    [Fact]
    public void BypassChoiceBeforeCommit_ZeroGrants()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.BypassOnce);

        Assert.False(scope.HasBypassGrant(scope.InitialStamp, Level1Item.Canonical));
    }

    // ---- (resolution-state level, T. NO PRECOMMIT GRANTS) choice mutation alone never touches
    // grant count ----
    [Fact]
    public void ResolutionState_ChoiceOnly_NeverIncreasesGrantCount()
    {
        var state = ClipboardDecisionResolutionState.Empty.WithChoice(Level1Item, ClipboardRuntimeItemChoice.BypassOnce);
        Assert.Equal(0, state.GrantCount);
    }

    // ---- 2. CommitResolved when not ready -> rejected ----
    [Fact]
    public void CommitResolved_WhenNotReady_Throws()
    {
        var scope = CreateScope(Level1Item);
        Assert.Throws<InvalidOperationException>(() => scope.CommitResolved(scope.InitialStamp));
    }

    // ---- 3. all Protect commit -> committed + zero grants ----
    [Fact]
    public void CommitResolved_AllProtect_CommittedWithZeroGrants()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);

        scope.CommitResolved(scope.InitialStamp);

        Assert.True(scope.Committed);
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, Level1Item.Canonical));
    }

    // ---- 4. all Bypass commit -> one grant per unique CanonicalValue ----
    [Fact]
    public void CommitResolved_AllBypass_GrantExists()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.BypassOnce);

        scope.CommitResolved(scope.InitialStamp);

        Assert.True(scope.Committed);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, Level1Item.Canonical));
    }

    // ---- 5. mixed Protect+Bypass commit -> grants only for Bypass choices ----
    [Fact]
    public void CommitResolved_MixedProtectAndBypass_GrantsOnlyForBypassChoices()
    {
        var scope = CreateScope(Level1Item, Level3Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce);
        scope.ApplyIntent(Level3Item, ClipboardDecisionIntent.BypassOnce); // -> final

        scope.CommitResolved(scope.InitialStamp);

        Assert.False(scope.HasBypassGrant(scope.InitialStamp, Level1Item.Canonical));
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, Level3Item.Canonical));
    }

    // ---- 6/7. exact final RevisionStamp used for every grant; a different revision never
    // matches ----
    [Fact]
    public void CommitResolved_UsesExactSuppliedFinalRevisionForEveryGrant()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.BypassOnce);

        var tracker = new RevisionTracker();
        var postWriteRevision = tracker.Observe("a completely different observed post-write text");

        scope.CommitResolved(postWriteRevision);

        Assert.True(scope.HasBypassGrant(postWriteRevision, Level1Item.Canonical));
        Assert.False(scope.HasBypassGrant(scope.InitialStamp, Level1Item.Canonical));
    }

    [Fact]
    public void HasBypassGrant_DifferentRevision_DoesNotMatch()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.BypassOnce);
        scope.CommitResolved(scope.InitialStamp);

        var otherTracker = new RevisionTracker();
        var otherRevision = otherTracker.Observe("unrelated text");

        Assert.False(scope.HasBypassGrant(otherRevision, Level1Item.Canonical));
    }

    // ---- 8. different canonical does not match grant ----
    [Fact]
    public void HasBypassGrant_DifferentCanonical_DoesNotMatch()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.BypassOnce);
        scope.CommitResolved(scope.InitialStamp);

        var otherCanonical = new CanonicalValue(PiiType.Phone, "01099998888");

        Assert.False(scope.HasBypassGrant(scope.InitialStamp, otherCanonical));
    }

    // ---- 9/10. same canonical deduplicates; RiskLevel difference does not duplicate grant
    // identity -- two DecisionItems that legitimately share one CanonicalValue but differ by
    // RiskLevel (STEP18's DUPLICATE_CANONICAL_POLICY) both resolving to BypassOnce collapses to
    // exactly one grant, since ClipboardBypassGrant equality never considers RiskLevel ----
    [Fact]
    public void CommitResolved_SameCanonicalDifferentRiskLevel_CollapsesToOneGrant()
    {
        var canonical = new CanonicalValue(PiiType.Phone, "01012345678");
        var itemAsLevel1 = new ClipboardDecisionItem(canonical, RiskLevel.Level1);
        var itemAsLevel3 = new ClipboardDecisionItem(canonical, RiskLevel.Level3);
        var scope = CreateScope(itemAsLevel1, itemAsLevel3);

        scope.ApplyIntent(itemAsLevel1, ClipboardDecisionIntent.BypassOnce);
        scope.ApplyIntent(itemAsLevel3, ClipboardDecisionIntent.BypassOnce);
        scope.ApplyIntent(itemAsLevel3, ClipboardDecisionIntent.BypassOnce); // second click -> final

        scope.CommitResolved(scope.InitialStamp);

        Assert.True(scope.HasBypassGrant(scope.InitialStamp, canonical));
    }

    // ---- 11. second CommitResolved rejected ----
    [Fact]
    public void CommitResolved_SecondCall_Throws()
    {
        var scope = CreateScope(Level1Item);
        scope.ApplyIntent(Level1Item, ClipboardDecisionIntent.Protect);
        scope.CommitResolved(scope.InitialStamp);

        Assert.Throws<InvalidOperationException>(() => scope.CommitResolved(scope.InitialStamp));
    }

    // ==================================================================
    // AE. ATOMIC STATE
    // ==================================================================

    [Fact]
    public void Empty_HasZeroChoicesZeroGrantsNotCommitted()
    {
        Assert.Equal(0, ClipboardDecisionResolutionState.Empty.ChoiceCount);
        Assert.Equal(0, ClipboardDecisionResolutionState.Empty.GrantCount);
        Assert.False(ClipboardDecisionResolutionState.Empty.Committed);
    }

    [Fact]
    public void ResolutionState_WithCommit_SetsCommittedAndExactGrantSetTogether()
    {
        var tracker = new RevisionTracker();
        var revision = tracker.Observe("x");
        var grant = new ClipboardBypassGrant(revision, Level1Item.Canonical);

        var state = ClipboardDecisionResolutionState.Empty
            .WithChoice(Level1Item, ClipboardRuntimeItemChoice.BypassOnce)
            .WithCommit(new HashSet<ClipboardBypassGrant> { grant });

        Assert.True(state.Committed);
        Assert.Equal(1, state.GrantCount);
        Assert.True(state.HasGrant(grant.Revision, grant.Canonical));
    }

    // ---- old immutable snapshot cannot be mutated by a later update built from it ----
    [Fact]
    public void OldResolutionStateSnapshot_UnaffectedByLaterMutation()
    {
        var original = ClipboardDecisionResolutionState.Empty;
        var afterFirstChoice = original.WithChoice(Level1Item, ClipboardRuntimeItemChoice.Protect);

        Assert.Equal(0, original.ChoiceCount);
        Assert.Equal(1, afterFirstChoice.ChoiceCount);
        Assert.Equal(ClipboardRuntimeItemChoice.Unresolved, original.GetChoice(Level1Item));
        Assert.Equal(ClipboardRuntimeItemChoice.Protect, afterFirstChoice.GetChoice(Level1Item));
    }

    // ---- state is represented through exactly one scope field ----
    [Fact]
    public void Scope_HasExactlyOneResolutionStateField()
    {
        var fields = typeof(ClipboardDecisionScope)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(ClipboardDecisionResolutionState))
            .ToList();

        var field = Assert.Single(fields);
        Assert.Equal("_resolutionState", field.Name);
    }

    // ---- no separate mutable choice/grant/Committed fields on scope ----
    [Fact]
    public void Scope_HasNoSeparateChoiceDictionaryGrantSetOrCommittedBoolField()
    {
        var fields = typeof(ClipboardDecisionScope)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType.IsGenericType &&
            (f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
             f.FieldType.GetGenericTypeDefinition() == typeof(HashSet<>)));
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(bool));
    }

    // ---- no mutable dictionary/set is exposed from ClipboardDecisionResolutionState's public
    // surface -- only counts and the Committed flag ----
    [Fact]
    public void ResolutionState_PublicPropertiesAreOnlyCountsAndCommittedFlag()
    {
        var properties = typeof(ClipboardDecisionResolutionState).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.All(properties, p => Assert.True(p.PropertyType == typeof(int) || p.PropertyType == typeof(bool)));
    }

    // ==================================================================
    // AF. SENSITIVE STRUCTURAL TESTS
    // ==================================================================

    [Fact]
    public void Scope_HasNoNewSensitiveFieldTypes()
    {
        var fields = typeof(ClipboardDecisionScope)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
        Assert.DoesNotContain(fields, f => f.FieldType.Name.Contains("AliasMap"));
        Assert.DoesNotContain(fields, f => f.FieldType.Name.Contains("ForegroundTargetSnapshot"));
        Assert.DoesNotContain(fields, f => f.FieldType.Name.Contains("ClipboardDecisionPlan"));
    }

    [Fact]
    public void ResolutionState_HasNoRawStringAliasMapOrGenerationField()
    {
        var fields = typeof(ClipboardDecisionResolutionState)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
        Assert.DoesNotContain(fields, f => f.FieldType.Name.Contains("AliasMap"));
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(long)); // no Generation
    }

    [Fact]
    public void Grant_HasOnlyRevisionStampAndCanonicalValueFields()
    {
        var fields = typeof(ClipboardBypassGrant).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.Equal(2, fields.Length);
        Assert.Contains(fields, f => f.FieldType == typeof(RevisionStamp));
        Assert.Contains(fields, f => f.FieldType == typeof(CanonicalValue));
    }

    [Fact]
    public void Grant_ToString_DoesNotExposeCanonicalValueOrSnapshotHash()
    {
        const string sentinel = "RAW-GRANT-SENTINEL-582917";
        var tracker = new RevisionTracker();
        var revision = tracker.Observe(sentinel);
        var canonical = new CanonicalValue(PiiType.Phone, sentinel);
        var grant = new ClipboardBypassGrant(revision, canonical);

        var text = grant.ToString();

        Assert.DoesNotContain(sentinel, text);
        Assert.DoesNotContain("SnapshotHash", text);
        Assert.Contains("PiiType", text);
        Assert.Contains("RevisionId", text);
    }

    [Fact]
    public void ResolutionState_ToString_ExposesOnlyCountsAndCommitted()
    {
        const string sentinel = "RAW-RESOLUTION-STATE-SENTINEL-582917";
        var canonical = new CanonicalValue(PiiType.Phone, sentinel);
        var item = new ClipboardDecisionItem(canonical, RiskLevel.Level1);
        var state = ClipboardDecisionResolutionState.Empty.WithChoice(item, ClipboardRuntimeItemChoice.BypassOnce);

        var text = state.ToString();

        Assert.DoesNotContain(sentinel, text);
        Assert.Contains("ChoiceCount", text);
        Assert.Contains("GrantCount", text);
        Assert.Contains("Committed", text);
    }

    [Fact]
    public void ScopeToString_AfterResolution_StillSafe()
    {
        const string sentinel = "RAW-SCOPE-RESOLUTION-SENTINEL-582917";
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe(sentinel);
        var canonical = new CanonicalValue(PiiType.Phone, sentinel);
        var item = new ClipboardDecisionItem(canonical, RiskLevel.Level1);
        var scope = new ClipboardDecisionScope(tracker, stamp, [item], generation: 0);

        scope.ApplyIntent(item, ClipboardDecisionIntent.BypassOnce);
        scope.CommitResolved(scope.InitialStamp);

        var text = scope.ToString();

        Assert.DoesNotContain(sentinel, text);
        Assert.Contains("ChoiceCount", text);
        Assert.Contains("GrantCount", text);
        Assert.Contains("Committed", text);
    }

    [Theory]
    [InlineData(typeof(ClipboardDecisionIntent))]
    [InlineData(typeof(ClipboardRuntimeItemChoice))]
    [InlineData(typeof(ClipboardBypassGrant))]
    [InlineData(typeof(ClipboardDecisionResolutionState))]
    public void NewTypes_HaveNoDebuggerDisplayOrTypeProxyAttributes(Type type)
    {
        var attributes = type.GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }

    // ==================================================================
    // ACCESSIBILITY
    // ==================================================================

    [Theory]
    [InlineData(typeof(ClipboardDecisionIntent))]
    [InlineData(typeof(ClipboardRuntimeItemChoice))]
    [InlineData(typeof(ClipboardBypassGrant))]
    [InlineData(typeof(ClipboardDecisionResolutionState))]
    public void NewTypes_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }
}
