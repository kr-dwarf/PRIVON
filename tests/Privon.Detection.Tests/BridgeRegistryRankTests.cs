using System.Reflection;
using Privon.Detection;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D3B0-A A.15 §16.3 / §16.3.1 / §16.3.2, §26 rows 4a / 4b / 4c -- the registry-rank oracle.
///
/// WHY THIS FILE EXISTS. The independent D3B0-B implementation audit found that the registry
/// enforced no frozen ID->rank mapping at all: arbitrary ID/rank pairs could register, known IDs
/// could register at wrong ranks, numeric precedence could drift, and the bridge suite did not
/// notice. These tests are the oracle that makes that omission impossible to reintroduce.
///
/// HOW EVERY PROBE STAYS LEGAL. §24.3 A forbids a test double that is REGISTERED in a real
/// DetectorRegistry. Every probe below is supplied to Create precisely so it is REJECTED; not one
/// of them survives validation, and no probe is ever read back out of a registry. That is the
/// exact line the accepted contract draws -- rows 4a and 4b REQUIRE a rejected supply, because a
/// rejection cannot be observed without one. No private backdoor, no test-only production API, and
/// no production shell is used anywhere in this file.
///
/// REFLECTION, SCOPED. The public-surface oracles at the bottom of this file read PUBLIC API
/// METADATA only. They inspect the EXTERNALLY VISIBLE types of the Privon.Detection production
/// assembly (Assembly.GetExportedTypes, so effective accessibility is respected and a public member
/// on an internal type is correctly ignored), plus DetectorRankMismatchException's own declared
/// constructors and instance members, to prove that no external caller can obtain or forge an
/// instance whose ExpectedRank != T(Id). They reason from TYPE STRUCTURE, never from member or
/// parameter names. No other assembly is scanned: the target's constructor is internal and this
/// assembly declares no InternalsVisibleTo, so an unrelated assembly has no ordinary API route to
/// one, and the oracle is a PRIVON-surface guard rather than an ecosystem-wide policy.
///
/// Reflection is never used to invoke the internal constructor, to reach any non-public member, to
/// inspect or mutate the private frozen mapping, to bypass registry validation, or to fabricate
/// duplicate-rank reachability. Nothing in the mutation matrix is ever invoked.
/// </summary>
public sealed class BridgeRegistryRankTests
{
    /// <summary>
    /// The frozen D2.2 ID->rank table, the tier-2 authority the accepted contract quotes but never
    /// edits ("the registrable identity domain IS the frozen D2.2 table", §16.3.1).
    ///
    /// DERIVATION -- stated explicitly so this cannot silently drift into a second source of truth:
    /// these 26 pairs were extracted mechanically from the D2.2 "frozen cross-pack resolution-ID
    /// precedence" table and cross-checked against every pair the accepted contract states
    /// independently -- the nine implemented detectors of §5.2, the four implemented-less ranks
    /// §5.2 names (global.path 240, kr.address 320, kr.name 340, kr.company 360), the §14 worked
    /// example (us.atin 50, us.ssn 70), and the AMENDED_BY_D2.2 spelling us.a_number. All 15
    /// contract-stated pairs agree with D2.2 exactly.
    ///
    /// This table is the EXPECTATION. Production holds its own copy; the tests below prove the two
    /// agree pair-for-pair by observing production's own reported ExpectedRank, never by reading
    /// production's table directly. If either copy is edited alone, these tests fail.
    /// </summary>
    private static readonly (string Id, int Rank)[] FrozenTable =
    [
        ("global.secret",       10),
        ("global.card",         20),
        ("kr.rrn",              30),
        ("kr.bank_account",     40),
        ("us.atin",             50),
        ("us.itin",             60),
        ("us.ssn",              70),
        ("us.mbi",              80),
        ("us.sevis",            90),
        ("us.passport",        100),
        ("us.a_number",        110),
        ("us.i94",             120),
        ("us.driver_state_id", 130),
        ("us.bank_account",    140),
        ("global.email",       200),
        ("global.ip",          210),
        ("global.mac",         220),
        ("global.gps",         230),
        ("global.path",        240),
        ("kr.phone",           300),
        ("us.phone",           310),
        ("kr.address",         320),
        ("us.address",         330),
        ("kr.name",            340),
        ("us.name",            350),
        ("kr.company",         360),
    ];

    // A probe carrying an arbitrary (id, rank). It reuses the EXISTING
    // DetectionPipelineTests.FakeDetector rather than declaring another test-local IDetector,
    // because §24.3 A fixes that inventory at 4 implementations / 3 files. Every probe below is
    // supplied only to be rejected.
    private static IDetector Probe(string id, int rank) =>
        new DetectionPipelineTests.FakeDetector(
            name: "RegistryRankProbe",
            piiType: PiiType.Secret,
            detect: _ => [],
            id: DetectorId.Create(id),
            rank: rank);

    private static IReadOnlyList<IDetector> NineLegacyDetectors() =>
    [
        new Detectors.SecretDetector(),
        new Detectors.CardNumberDetector(),
        new Detectors.ResidentRegistrationNumberDetector(),
        new Detectors.BankAccountNumberDetector(),
        new Detectors.EmailDetector(),
        new Detectors.IpAddressDetector(),
        new Detectors.MacAddressDetector(),
        new Detectors.GpsCoordinateDetector(),
        new Detectors.PhoneDetector(),
    ];

    // ---------------------------------------------------------------------------------------
    // §26 row 4a / 12 -- the 26-ID/rank table oracle
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ExpectedFrozenTable_Is26Ids_26DistinctRanks_AndInjective()
    {
        // The premise the whole §16.3.2 unreachability proof rests on. Asserted on the expectation
        // itself so a careless edit to this file is caught here rather than silently weakening the
        // oracles below.
        Assert.Equal(26, FrozenTable.Length);
        Assert.Equal(26, FrozenTable.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(26, FrozenTable.Select(t => t.Rank).Distinct().Count());

        // Every frozen id is a grammatically valid DetectorId (rule 1 can never be what rejects one).
        foreach (var (id, _) in FrozenTable)
            Assert.True(DetectorId.TryCreate(id, out _), id);
    }

    [Fact]
    public void ProductionFrozenTable_MatchesTheAcceptedD22Table_ForAll26Identities()
    {
        // Production's T(Id) is observed through its OWN reported ExpectedRank: supply each frozen
        // id at a deliberately wrong rank and read back what production says the rank should be.
        // Every one of the 26 probes is rejected, so nothing is ever registered.
        //
        // This single oracle detects every drift class §12 requires:
        //   missing frozen identity  -> that id would throw UnknownDetectorIdException instead
        //   wrong / changed rank     -> ExpectedRank would not equal the accepted rank
        //   extra identity           -> covered by UnknownIdentity_... below
        //   duplicate rank in table  -> impossible while the expectation asserts 26 distinct ranks
        foreach (var (id, expectedRank) in FrozenTable)
        {
            int wrongRank = expectedRank + 1;

            var ex = Assert.Throws<DetectorRankMismatchException>(
                () => DetectorRegistry.Create([Probe(id, wrongRank)]));

            Assert.Equal(id, ex.Id.Value);
            Assert.Equal(expectedRank, ex.ExpectedRank);
            Assert.Equal(wrongRank, ex.ActualRank);
        }
    }

    [Fact]
    public void ProductionFrozenTable_AcceptsEachFrozenIdAtItsExactRank()
    {
        // The complementary direction: at the CORRECT rank, rule 2b does not fire. Proven without
        // registering anything, by pairing the correct-rank probe with a second probe that trips a
        // LATER rule -- if 2b had fired, the later rule's exception could not be observed.
        //
        // The partner is the same id repeated at the same correct rank, so rule 3 (duplicate id) is
        // what must be observed.
        foreach (var (id, rank) in FrozenTable)
        {
            var ex = Assert.Throws<DuplicateDetectorIdException>(
                () => DetectorRegistry.Create([Probe(id, rank), Probe(id, rank)]));

            Assert.Equal(id, ex.Id.Value);
        }
    }

    // ---------------------------------------------------------------------------------------
    // §26 row 4b -- unknown identity fails closed
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void UnknownIdentity_OutsideTheFrozenTable_FailsClosedAndIsNeverAliased()
    {
        // FROZEN_ID_RANK_MAPPING_IS_TOTAL_AND_CLOSED: an id outside the table has NO authorized
        // rank, so it cannot be validated and MUST be rejected. Each probe exists only to prove
        // the rejection -- none is a fake production detector that survives registration.
        string[] outsideTheTable =
        [
            "test.snapshot_dependent",  // the synthetic identity the old readiness tests registered
            "us.anumber",               // AMENDED_BY_D2.2: the NON-authoritative A-Number spelling
            "us.tin",                   // plausible-looking, not frozen
            "global.path2",             // near-miss on a real frozen id
            "kr.phon",                  // prefix of a real frozen id -- no prefix matching
            "aaa.missing",
        ];

        foreach (string id in outsideTheTable)
        {
            // Rank is irrelevant: even a rank that IS in the table cannot authorize a foreign id.
            var ex = Assert.Throws<UnknownDetectorIdException>(
                () => DetectorRegistry.Create([Probe(id, 300)]));

            Assert.Equal(id, ex.Id.Value);
        }
    }

    [Fact]
    public void MultipleUnknownIds_SelectTheUtf8ByteLexicographicMinimum_UnderEveryPermutation()
    {
        string[] ids = ["us.tin", "aaa.missing", "kr.phon"];

        foreach (var permutation in Permutations(ids))
        {
            var ex = Assert.Throws<UnknownDetectorIdException>(
                () => DetectorRegistry.Create([.. permutation.Select(i => Probe(i, 300))]));

            // "aaa.missing" < "kr.phon" < "us.tin" byte-lexicographically.
            Assert.Equal("aaa.missing", ex.Id.Value);
        }
    }

    // ---------------------------------------------------------------------------------------
    // §26 row 4a -- wrong rank on a frozen id
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void KnownIdWithWrongRank_ThrowsRankMismatch_NotUnknownAndNotDuplicate()
    {
        // The single-offender case §26 row 4a names explicitly: a legacy id with a wrong rank.
        var ex = Assert.Throws<DetectorRankMismatchException>(
            () => DetectorRegistry.Create([Probe("kr.phone", 301)]));

        Assert.Equal("kr.phone", ex.Id.Value);
        Assert.Equal(300, ex.ExpectedRank);
        Assert.Equal(301, ex.ActualRank);

        // The observable may not lie about WHICH invariant failed (§16.3.1).
        Assert.IsNotType<UnknownDetectorIdException>(ex);
        Assert.IsNotType<DuplicateDetectorIdException>(ex);
        Assert.IsNotType<DuplicateDetectorRankException>(ex);

        // The message is PRIVON-owned and invariant-culture formatted.
        Assert.Equal("DetectorRankMismatch(kr.phone, expected=300, actual=301)", ex.Message);
    }

    [Fact]
    public void SameIdWithTwoDifferentWrongRanks_IsPermutationInvariant_IncludingActualRank()
    {
        // RULE_2B_OFFENDER_SELECTOR_IS_TOTAL (A13-P1-1), exactly the contract's frozen worked case:
        // { (kr.phone, 999), (kr.phone, 301) }, both wrong because T(kr.phone) = 300.
        //
        // Selecting on Id alone would TIE here, and which ActualRank reached the payload could then
        // follow supply order. This test fails if the second key component is ever dropped -- it
        // asserts the ActualRank, not merely the exception type.
        var forward = Assert.Throws<DetectorRankMismatchException>(
            () => DetectorRegistry.Create([Probe("kr.phone", 999), Probe("kr.phone", 301)]));

        var reversed = Assert.Throws<DetectorRankMismatchException>(
            () => DetectorRegistry.Create([Probe("kr.phone", 301), Probe("kr.phone", 999)]));

        foreach (var ex in new[] { forward, reversed })
        {
            Assert.Equal("kr.phone", ex.Id.Value);
            Assert.Equal(300, ex.ExpectedRank);
            Assert.Equal(301, ex.ActualRank);   // the SMALLER wrong rank, under both permutations
        }

        // Byte-identical observable payload, not merely the same class.
        Assert.Equal(forward.Id, reversed.Id);
        Assert.Equal(forward.ExpectedRank, reversed.ExpectedRank);
        Assert.Equal(forward.ActualRank, reversed.ActualRank);
        Assert.Equal(forward.Message, reversed.Message);
    }

    [Fact]
    public void MultipleDistinctWrongRankOffenders_SelectByIdThenActualRank()
    {
        // The contract's multi-id case: { (kr.rrn, 31), (global.card, 21), (global.card, 22) }.
        // Key component 1 separates distinct ids ('g' < 'k'); component 2 separates equal ids.
        (string Id, int Rank)[] offenders = [("kr.rrn", 31), ("global.card", 21), ("global.card", 22)];

        foreach (var permutation in Permutations(offenders))
        {
            var ex = Assert.Throws<DetectorRankMismatchException>(
                () => DetectorRegistry.Create([.. permutation.Select(o => Probe(o.Id, o.Rank))]));

            Assert.Equal("global.card", ex.Id.Value);
            Assert.Equal(20, ex.ExpectedRank);
            Assert.Equal(21, ex.ActualRank);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Class-major validation precedence (§16.3.1) -- supply order must never decide the failure
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void UnknownIdentity_PrecedesRankMismatch_UnderEveryPermutation()
    {
        // Rule 2a is evaluated over the WHOLE input before rule 2b begins. A "throw on first
        // enumeration hit" implementation would report whichever offender happened to come first.
        IDetector[] forward = [Probe("zzz.unknown", 300), Probe("kr.phone", 301)];
        IDetector[] reversed = [Probe("kr.phone", 301), Probe("zzz.unknown", 300)];

        foreach (var input in new[] { forward, reversed })
        {
            var ex = Assert.Throws<UnknownDetectorIdException>(() => DetectorRegistry.Create(input));
            Assert.Equal("zzz.unknown", ex.Id.Value);
        }
    }

    [Fact]
    public void RankMismatch_PrecedesDuplicateId_UnderEveryPermutation()
    {
        // Same frozen id supplied twice, one carrying a wrong rank: rule 2b must fire before rule 3.
        IDetector[] forward = [Probe("kr.phone", 300), Probe("kr.phone", 301)];
        IDetector[] reversed = [Probe("kr.phone", 301), Probe("kr.phone", 300)];

        foreach (var input in new[] { forward, reversed })
        {
            var ex = Assert.Throws<DetectorRankMismatchException>(() => DetectorRegistry.Create(input));
            Assert.Equal(300, ex.ExpectedRank);
            Assert.Equal(301, ex.ActualRank);
        }
    }

    [Fact]
    public void InvalidGrammar_PrecedesEveryLaterRule()
    {
        // Rule 1 first: default(DetectorId) has no value at all, so it is not even a candidate for
        // the closed-domain check.
        var ex = Assert.Throws<ArgumentException>(
            () => DetectorRegistry.Create([new DetectionPipelineTests.FakeDetector(
                name: "NoIdentityProbe",
                piiType: PiiType.Secret,
                detect: _ => [],
                id: default(DetectorId),
                rank: 10)]));

        Assert.Equal("detectors", ex.ParamName);
    }

    [Fact]
    public void DuplicateId_AtCorrectRanks_ThrowsDuplicateId_NeverDedupes()
    {
        // Real production detectors, correct frozen ranks, one supplied twice. Rules 1-2b all pass,
        // so rule 3 is what must be observed. Never repaired, never deduped.
        var withDuplicate = new List<IDetector>(NineLegacyDetectors()) { new Detectors.PhoneDetector() };

        var ex = Assert.Throws<DuplicateDetectorIdException>(
            () => DetectorRegistry.Create(withDuplicate));

        Assert.Equal("kr.phone", ex.Id.Value);
    }

    // ---------------------------------------------------------------------------------------
    // §26 row 4c -- duplicate-rank structural unreachability, PROVEN not FABRICATED
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void DuplicateRank_IsStructurallyUnreachable_AndIsNeverFabricated()
    {
        // DUPLICATE_RANK_IS_UNREACHABLE_UNDER_THE_CURRENT_TABLE (§16.3.2). The proof:
        // after rule 2b every registered rank is T(Id); if two entries shared a rank then
        // T(Id_a) = T(Id_b), and injectivity of T forces Id_a = Id_b -- which rule 3 already
        // rejected. So no input Create ACCEPTS can reach rule 4.
        //
        // This test does NOT manufacture a duplicate-rank input. It proves the two escape routes an
        // implementation might leave open are actually closed, which is what makes the proof bind.

        // Route 1 -- two DIFFERENT frozen ids forced onto one rank. The second necessarily carries a
        // rank that is not its own T(Id), so rule 2b rejects it first.
        var route1 = Assert.Throws<DetectorRankMismatchException>(
            () => DetectorRegistry.Create([Probe("kr.phone", 300), Probe("global.card", 300)]));
        Assert.Equal("global.card", route1.Id.Value);
        Assert.Equal(20, route1.ExpectedRank);
        Assert.Equal(300, route1.ActualRank);

        // Route 2 -- an unknown id carrying a frozen rank. Rule 2a rejects it before any rank
        // comparison happens at all.
        var route2 = Assert.Throws<UnknownDetectorIdException>(
            () => DetectorRegistry.Create([Probe("kr.phone", 300), Probe("zzz.clone", 300)]));
        Assert.Equal("zzz.clone", route2.Id.Value);

        // Route 3 -- the same id twice at its own correct rank is a DUPLICATE ID, caught by rule 3.
        Assert.Throws<DuplicateDetectorIdException>(
            () => DetectorRegistry.Create([Probe("kr.phone", 300), Probe("kr.phone", 300)]));

        // The type remains DECLARED for a future amended D2.2 table that permits equal ranks. It is
        // retained because the proof is conditional on the CURRENT table's injectivity, not because
        // a test name expects it. No test bypasses rules 2a/2b to make it throw.
        Assert.NotNull(typeof(DuplicateDetectorRankException));
        Assert.True(typeof(Exception).IsAssignableFrom(typeof(DuplicateDetectorRankException)));
    }

    // ---------------------------------------------------------------------------------------
    // The positive path stays intact
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheNineLegacyDetectors_AllSatisfyTheFrozenTable_AndRegister()
    {
        // Enforcement must not have broken the real production composition.
        var registry = DetectorRegistry.Create(NineLegacyDetectors());
        Assert.Equal(9, registry.All.Count);

        var expectedByFrozenTable = FrozenTable.ToDictionary(t => t.Id, t => t.Rank, StringComparer.Ordinal);
        foreach (var registration in registry.All)
            Assert.Equal(expectedByFrozenTable[registration.Id.Value], registration.ResolutionRank);
    }

    [Fact]
    public void NoProbeEverSurvivesRegistration()
    {
        // Belt and braces for §24.3 A: every id this file probes with is absent from a real
        // registry built from the real nine.
        var registry = DetectorRegistry.Create(NineLegacyDetectors());

        foreach (string probed in new[] { "test.snapshot_dependent", "us.anumber", "us.tin",
                                          "global.path2", "kr.phon", "aaa.missing", "zzz.unknown",
                                          "zzz.clone" })
            Assert.False(registry.TryGet(DetectorId.Create(probed), out _), probed);
    }

    // ---------------------------------------------------------------------------------------
    // PUBLIC-SURFACE STRUCTURAL ORACLE -- ExpectedRank may never be caller-controlled
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void DetectorRankMismatchException_ExposesNoPublicConstructor_SoExpectedRankCannotBeForged()
    {
        // WHY THIS EXISTS. The previous oracles proved every value the REGISTRY throws, but said
        // nothing about what an arbitrary external caller could CONSTRUCT. The type shipped a public
        // constructor taking expectedRank, so any external code could build an instance whose
        // ExpectedRank != T(Id) -- an exception object that lies about which rank the frozen D2.2
        // table authorizes. §16.3.1 requires ExpectedRank = T(Id), "never caller-controlled",
        // so this is a public-surface defect and is closed structurally.
        //
        // This inspects PUBLIC API METADATA only. It does not bypass registry validation, does not
        // reach a non-public member, and does not fabricate duplicate-rank reachability.
        var type = typeof(DetectorRankMismatchException);

        // The type stays PUBLIC and catchable -- §23.3 declares it public and tests must observe it.
        Assert.True(type.IsPublic, "DetectorRankMismatchException must remain publicly observable.");

        // ...but it offers NO public construction path at all.
        var publicConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.Empty(publicConstructors);

        // Specifically, the exact shape the audit rejected must not come back.
        Assert.Null(type.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(DetectorId), typeof(int), typeof(int)],
            modifiers: null));

        // Public STATIC construction paths are covered STRUCTURALLY by
        // DeclaresNoPublicStaticSurfaceCapableOfProducingTheException below. This test deliberately
        // makes NO claim about them: an earlier version of it matched a parameter literally named
        // "expectedRank", which a factory named Create(id, expected, actual) would have walked
        // straight past. Parameter spelling is not a security boundary.
    }

    // =======================================================================================
    // THE ORACLE'S ACTUAL QUESTION
    //
    //   Does the externally visible public API of the PRIVON production assembly declare a
    //   structural path that can directly yield a DetectorRankMismatchException instance to an
    //   external caller, bypassing the registry-owned construction invariant?
    //
    // That is deliberately NARROWER than "could this CLR type theoretically contain one" and than
    // "could some future method body someday return one". Reflection can prove the declared,
    // externally reachable API graph; it cannot prove whole-program semantics, and this oracle does
    // not pretend otherwise.
    //
    // TWO PRECISION DEFECTS THIS REPLACED, both found by independent audit:
    //
    //   FALSE NEGATIVES. The previous version scanned only members declared ON the exception type.
    //   A C# user-defined conversion may be declared on the SOURCE type instead
    //   ("public static implicit operator DetectorRankMismatchException(Payload v)"), and a factory
    //   may return a NON-GENERIC carrier whose public property exposes the exception. Both escaped.
    //   Fixed by scanning the production assembly's externally visible types, and by walking a
    //   returned type's public output channels rather than only its generic arguments.
    //
    //   FALSE POSITIVES. The previous version treated "t.IsAssignableFrom(target)" as dangerous, so
    //   a harmless "public static object Metadata" or "public static Exception Error()" failed; it
    //   treated every generic parameter as dangerous, so "public static T Make<T>()" failed; and it
    //   banned public nested types outright. The accepted contract prohibits none of those. Type
    //   POSSIBILITY is not a structural production path, so all three rules are removed.
    // =======================================================================================

    private static readonly Type Target = typeof(DetectorRankMismatchException);

    /// <summary>
    /// Well-known framework types that terminate the output-graph walk. None of them DECLARES a
    /// member returning DetectorRankMismatchException, and walking them would mean speculating about
    /// every runtime subtype a base type might hold -- which §12 of the remediation brief forbids
    /// and which reflection cannot prove anyway. Declared public API is the evidence boundary.
    /// </summary>
    private static bool IsTerminalFrameworkType(Type t) =>
        t.IsPrimitive
        || t.IsEnum
        || t == typeof(string) || t == typeof(object) || t == typeof(decimal)
        || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan)
        || t == typeof(Guid) || t == typeof(Uri) || t == typeof(Type)
        || t == typeof(Exception) || t == typeof(SystemException) || t == typeof(AggregateException)
        || (t.Namespace is string ns
            && (ns == "System.Reflection" || ns.StartsWith("System.Reflection.", StringComparison.Ordinal)));

    /// <summary>
    /// How many distinct types the output-graph walk may visit before it declares itself
    /// INCONCLUSIVE. This is a resource guard, not a semantic limit: the visited set already
    /// guarantees termination over the finite universe of declared metadata types, and the real
    /// production graph visits a tiny fraction of this. Exhaustion THROWS -- it never returns
    /// "safe" -- because an oracle that converts uncertainty into GREEN is the exact failure mode
    /// the previous depth cutoff embodied.
    /// </summary>
    private const int VisitedTypeBudget = 10_000;

    /// <summary>
    /// Is this parameter an OUTPUT channel -- can the caller receive an instance through it?
    ///
    /// Measured on this repository's toolchain (net10.0) rather than assumed, by compiling each
    /// shape and reading the emitted ParameterInfo:
    ///
    ///   shape              IsByRef  IsOut  IsIn   attributes
    ///   out T              True     True   False  OutAttribute
    ///   ref T              True     False  False  (none)
    ///   in T               True     False  True   IsReadOnlyAttribute, InAttribute
    ///   ref readonly T     True     False  True   RequiresLocationAttribute, InAttribute
    ///   T (by value)       False    False  False  (none)
    ///
    /// So IsIn alone separates the read-only by-ref forms from the output-capable ones, and no
    /// custom-modifier or attribute probing is required on this framework. Parameter NAMES are
    /// never consulted.
    ///
    /// out / ref        -> output-capable: the callee may hand back an instance.
    /// in / ref readonly-> input-only: the caller supplies a reference it cannot have replaced.
    /// by value         -> not by-ref at all; input-only.
    ///
    /// This rule is about PARAMETER direction only. A by-ref RETURN (ref T or ref readonly T) is an
    /// output regardless, and is handled by the return-type walk below, which unwraps IsByRef
    /// without consulting IsIn.
    /// </summary>
    private static bool IsOutputCapableByRef(ParameterInfo parameter) =>
        parameter.ParameterType.IsByRef && !parameter.IsIn;

    /// <summary>
    /// Does a declared OUTPUT type structurally hand the caller a DetectorRankMismatchException?
    ///
    /// GRAPH REACHABILITY, not recursion depth. The previous version carried a hard
    /// "depth > 6 => false", which silently classified an ordinary seven-link public carrier chain
    /// as safe -- a false negative an independent audit demonstrated. There is now NO semantic
    /// depth limit of any size: an iterative worklist walks the declared output graph until it is
    /// exhausted, and the visited set -- not a counter -- is what terminates it, including on
    /// cycles.
    ///
    /// Rules per reachable type, each provable from declared metadata:
    ///   by-ref / array / pointer  containers -- unwrapped to the element type.
    ///   direct                    target.IsAssignableFrom(t) -- t IS the exception or a subtype.
    ///                             The reverse direction stays deliberately absent: object,
    ///                             Exception or an interface merely COULD hold one.
    ///   generic parameter         not dangerous on its own.
    ///   terminal framework type   walk stops; none declares a member returning the target.
    ///   anything else             enqueue its PUBLIC OUTPUT CHANNELS -- readable instance
    ///                             properties, instance fields, instance method return types, and
    ///                             output-capable by-ref parameters. Ordinary by-value and
    ///                             read-only `in` parameters are inputs and are never enqueued,
    ///                             so Action&lt;T&gt; and `in T` stay correctly allowed.
    /// </summary>
    private static bool ExposesTarget(Type? root)
    {
        if (root is null) return false;

        const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        var visited = new HashSet<Type>();
        var work = new Stack<Type>();
        work.Push(root);

        while (work.Count > 0)
        {
            var t = work.Pop();
            if (t is null || t == typeof(void)) continue;

            // Unwrap container layers -- T[], ref T, T*, and combinations such as T[][].
            while (t.IsByRef || t.IsPointer || t.IsArray)
            {
                var element = t.GetElementType();
                if (element is null) break;
                t = element;
            }

            if (t.IsGenericParameter) continue;
            if (Target.IsAssignableFrom(t)) return true;
            if (IsTerminalFrameworkType(t)) continue;
            if (!visited.Add(t)) continue;                 // cycles close here, not by depth

            // Fail LOUDLY rather than returning "safe" -- see VisitedTypeBudget.
            if (visited.Count > VisitedTypeBudget)
                throw new InvalidOperationException(
                    "ANALYSER INCONCLUSIVE: the output-graph walk exceeded its visited-type budget "
                    + $"of {VisitedTypeBudget} starting from {root.FullName}. This is a test failure, "
                    + "not evidence of safety.");

            foreach (var property in t.GetProperties(PublicInstance))
                if (property.CanRead) work.Push(property.PropertyType);

            foreach (var field in t.GetFields(PublicInstance))
                work.Push(field.FieldType);

            foreach (var method in t.GetMethods(PublicInstance))
            {
                work.Push(method.ReturnType);              // covers ref / ref readonly returns

                foreach (var parameter in method.GetParameters())
                    if (IsOutputCapableByRef(parameter))
                        work.Push(parameter.ParameterType);
            }
        }

        return false;
    }

    /// <summary>
    /// Every declared public STATIC entry point on <paramref name="types"/> that structurally yields
    /// the target. Conversion operators need no special case: op_Implicit and op_Explicit ARE public
    /// static methods, so a conversion declared on a SOURCE type is found as soon as that source type
    /// is in the scanned set. Member names, parameter names and payload shapes are never consulted.
    /// </summary>
    private static List<string> ScanForTargetProducingSurface(IEnumerable<Type> types)
    {
        const BindingFlags DeclaredPublicStatic =
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();

        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(DeclaredPublicStatic))
            {
                if (ExposesTarget(method.ReturnType))
                    offenders.Add($"{type.Name}.{method.Name} returns {method.ReturnType.Name}");

                foreach (var parameter in method.GetParameters())
                    if (IsOutputCapableByRef(parameter) && ExposesTarget(parameter.ParameterType))
                        offenders.Add($"{type.Name}.{method.Name} yields through out/ref parameter");
            }

            foreach (var property in type.GetProperties(DeclaredPublicStatic))
                if (property.CanRead && ExposesTarget(property.PropertyType))
                    offenders.Add($"{type.Name}.{property.Name} static property of {property.PropertyType.Name}");

            foreach (var field in type.GetFields(DeclaredPublicStatic))
                if (ExposesTarget(field.FieldType))
                    offenders.Add($"{type.Name}.{field.Name} static field of {field.FieldType.Name}");
        }

        return offenders;
    }

    [Fact]
    public void ProductionPublicApi_DeclaresNoPathThatYieldsAMismatchExceptionToAnExternalCaller()
    {
        // GetExportedTypes() is the effective-visibility boundary: it returns exactly the types an
        // external assembly can name. A public member on an INTERNAL type is not externally callable
        // and must not be reported, and a public nested type under a non-public container is not
        // exported -- both are handled by construction rather than by a hand-rolled visibility rule.
        //
        // Scope is the PRIVON production assembly only. A third-party assembly is not scanned and
        // could not help anyway: the target's constructor is internal and Privon.Detection declares
        // no InternalsVisibleTo, so an unrelated assembly has no ordinary API route to manufacture a
        // forged instance. The oracle polices the PRIVON production public surface -- it is not an
        // ecosystem-wide rule.
        var productionAssembly = Target.Assembly;
        var exported = productionAssembly.GetExportedTypes();

        Assert.Contains(Target, exported);            // the type stays externally observable
        Assert.True(Target.IsPublic && Target.IsSealed);

        var offenders = ScanForTargetProducingSurface(exported);

        Assert.True(
            offenders.Count == 0,
            "No externally visible production API may yield a DetectorRankMismatchException. Found: "
                + string.Join("; ", offenders));
    }

    [Fact]
    public void ScanBoundary_UsesEffectiveVisibility_NotMemberLevelPublicAlone()
    {
        // §22: a `public` member on an `internal` type is NOT externally callable. Utf8ByteOrder is
        // an internal production static class with public members; it must never appear in the
        // externally visible surface the oracle polices.
        var exported = Target.Assembly.GetExportedTypes().ToHashSet();

        var internalWithPublicMembers = Target.Assembly.GetType("Privon.Detection.Utf8ByteOrder", throwOnError: true)!;
        Assert.False(internalWithPublicMembers.IsVisible);
        Assert.DoesNotContain(internalWithPublicMembers, exported);

        // And every type the oracle does scan really is externally visible.
        Assert.All(exported, t => Assert.True(t.IsVisible, t.FullName));
    }

    // =======================================================================================
    // PERMANENT MUTATION MATRIX -- the test that tests the oracle.
    //
    // The previous defects were invisible because the real production type stayed clean while the
    // ORACLE was wrong. These fixtures declare the exact shapes an independent audit named, and the
    // matrix runs the SAME analyser over them, so a future weakening of the predicate fails here.
    //
    // Fixtures return null! deliberately: the matrix inspects declared API SHAPE and never invokes
    // anything, so no forged exception is ever constructed and the internal constructor is never
    // touched.
    // =======================================================================================

    public sealed class MutationPayload
    {
        public int Expected { get; init; }
        public int Actual { get; init; }
    }

    public sealed class MutationCarrier                     // non-generic carrier
    {
        public DetectorRankMismatchException Value => null!;
    }

    // ---- deep carrier chains (P1-1: no semantic depth cutoff) ----
    // Ten carrier edges before the target -- comfortably past the old "depth > 6" boundary, so the
    // regression is not pinned to one off-by-one reading of it.
    public sealed class Deep10 { public DetectorRankMismatchException Value => null!; }
    public sealed class Deep9 { public Deep10 Next => null!; }
    public sealed class Deep8 { public Deep9 Next => null!; }
    public sealed class Deep7 { public Deep8 Next => null!; }
    public sealed class Deep6 { public Deep7 Next => null!; }
    public sealed class Deep5 { public Deep6 Next => null!; }
    public sealed class Deep4 { public Deep5 Next => null!; }
    public sealed class Deep3 { public Deep4 Next => null!; }
    public sealed class Deep2 { public Deep3 Next => null!; }
    public sealed class Deep1 { public Deep2 Next => null!; }

    public static class D1_DeepTargetChain
    { public static Deep1 Build() => null!; }

    // Exactly as deep, but the last link exposes an int. Proves the analyser really traverses the
    // graph instead of the lazy "anything deep is dangerous" non-fix.
    public sealed class Safe10 { public int Value => 0; }
    public sealed class Safe9 { public Safe10 Next => null!; }
    public sealed class Safe8 { public Safe9 Next => null!; }
    public sealed class Safe7 { public Safe8 Next => null!; }
    public sealed class Safe6 { public Safe7 Next => null!; }
    public sealed class Safe5 { public Safe6 Next => null!; }
    public sealed class Safe4 { public Safe5 Next => null!; }
    public sealed class Safe3 { public Safe4 Next => null!; }
    public sealed class Safe2 { public Safe3 Next => null!; }
    public sealed class Safe1 { public Safe2 Next => null!; }

    public static class D2_DeepSafeChain
    { public static Safe1 Build() => null!; }

    // ---- cycles terminate through the visited set, never through depth ----
    public sealed class CycleA { public CycleB Next => null!; }
    public sealed class CycleB { public CycleA Back => null!; }

    public static class C1_CycleWithoutTarget
    { public static CycleA Build() => null!; }

    public sealed class CycleTargetA { public CycleTargetB Next => null!; }
    public sealed class CycleTargetB
    {
        public CycleTargetA Back => null!;                        // the cycle
        public DetectorRankMismatchException Leak => null!;        // ...and a real exposure past it
    }

    public static class C2_CycleWithReachableTarget
    { public static CycleTargetA Build() => null!; }

    // ---- by-ref direction (P1-2) ----
    public static class B2_WritableRefParameter
    { public static void Fill(ref DetectorRankMismatchException value) { } }

    public static class B3_ReadOnlyInParameter
    { public static void Accept(in DetectorRankMismatchException value) { } }

    public static class B4_ByValueParameter
    { public static void Accept(DetectorRankMismatchException value) { } }

    public static class B5_ByRefReturn
    { public static ref DetectorRankMismatchException Borrow() => throw new NotSupportedException(); }

    // ---- MUST REJECT ----
    public static class R1_RenamedDirectFactory
    { public static DetectorRankMismatchException Create(DetectorId id, int expected, int actual) => null!; }

    public static class R2_PayloadFactory
    { public static DetectorRankMismatchException Build(MutationPayload payload) => null!; }

    public sealed class R3_ImplicitConversionSource
    { public static implicit operator DetectorRankMismatchException(R3_ImplicitConversionSource value) => null!; }

    public sealed class R4_ExplicitConversionSource
    { public static explicit operator DetectorRankMismatchException(R4_ExplicitConversionSource value) => null!; }

    public static class R5_StaticProperty
    { public static DetectorRankMismatchException Example => null!; }

    public static class R6_StaticField
    { public static readonly DetectorRankMismatchException Sample = null!; }

    public static class R7_NonGenericCarrierFactory
    { public static MutationCarrier Build() => null!; }

    public static class R8_GenericWrapper
    { public static Task<DetectorRankMismatchException> MakeAsync() => null!; }

    public static class R9_ArrayExposure
    { public static DetectorRankMismatchException[] MakeMany() => null!; }

    public static class R10_OutParameter
    { public static void Make(out DetectorRankMismatchException ex) => ex = null!; }

    // ---- MUST ALLOW ----
    public static class A1_UnrelatedStatic
    { public static int Version => 1; }

    public static class A2_ObjectReturn
    { public static object Metadata => new(); }

    public static class A3_BaseExceptionReturn
    { public static Exception Error() => new InvalidOperationException(); }

    public static class A4_GenericReturn
    { public static T Make<T>() => default!; }

    public static class A5_NestedEnumHolder
    { public enum Kind { One, Two } }

    public static class A6_InputOnly
    { public static string Describe(DetectorRankMismatchException ex) => ex.Message; }

    public static IEnumerable<object[]> MutationMatrix() =>
    [
        [typeof(R1_RenamedDirectFactory),       true,  "R1 renamed direct factory"],
        [typeof(R2_PayloadFactory),             true,  "R2 payload factory"],
        [typeof(R3_ImplicitConversionSource),   true,  "R3 source-declared implicit conversion"],
        [typeof(R4_ExplicitConversionSource),   true,  "R4 source-declared explicit conversion"],
        [typeof(R5_StaticProperty),             true,  "R5 direct static property"],
        [typeof(R6_StaticField),                true,  "R6 direct static field"],
        [typeof(R7_NonGenericCarrierFactory),   true,  "R7 non-generic carrier"],
        [typeof(R8_GenericWrapper),             true,  "R8 Task<T> output wrapper"],
        [typeof(R9_ArrayExposure),              true,  "R9 array exposure"],
        [typeof(R10_OutParameter),              true,  "byref-out-target"],
        [typeof(B2_WritableRefParameter),       true,  "byref-ref-target"],
        [typeof(B5_ByRefReturn),                true,  "byref-return-target"],
        [typeof(D1_DeepTargetChain),            true,  "deep-target-chain"],
        [typeof(C2_CycleWithReachableTarget),   true,  "cycle-with-target"],
        [typeof(A1_UnrelatedStatic),            false, "A1 unrelated static"],
        [typeof(A2_ObjectReturn),               false, "A2 object return"],
        [typeof(A3_BaseExceptionReturn),        false, "A3 base Exception return"],
        [typeof(A4_GenericReturn),              false, "A4 unconstrained generic return"],
        [typeof(A5_NestedEnumHolder),           false, "A5 harmless public nested enum"],
        [typeof(A5_NestedEnumHolder.Kind),      false, "A5 the nested enum itself"],
        [typeof(A6_InputOnly),                  false, "A6 input-only use of the target"],
        [typeof(B3_ReadOnlyInParameter),        false, "byref-in-target"],
        [typeof(B4_ByValueParameter),           false, "byvalue-input-target"],
        [typeof(D2_DeepSafeChain),              false, "deep-safe-chain"],
        [typeof(C1_CycleWithoutTarget),         false, "cycle-safe"],
    ];

    [Theory]
    [MemberData(nameof(MutationMatrix))]
    public void OracleItself_RejectsEveryProductionPath_AndAllowsEveryHarmlessShape(
        Type fixtureType, bool mustBeRejected, string label)
    {
        var offenders = ScanForTargetProducingSurface([fixtureType]);

        if (mustBeRejected)
            Assert.True(offenders.Count > 0, $"{label}: oracle MISSED a target-producing path.");
        else
            Assert.True(offenders.Count == 0,
                $"{label}: oracle FALSELY flagged a legal shape -- {string.Join("; ", offenders)}");
    }

    [Fact]
    public void MutationMatrix_CoversEveryAuditNamedCase()
    {
        // Counts alone were a weak guard: one required case could be silently replaced by a
        // duplicate while the totals still passed. The guard now pins IDENTITY as well as size.
        var rows = MutationMatrix().ToArray();
        var labels = rows.Select(r => (string)r[2]).ToArray();

        // 1. every label is unique -- no silent duplicate substitution.
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());

        // 2. every boundary an independent audit named is present BY NAME and on the correct side.
        var expectedSide = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["deep-target-chain"]    = true,
            ["deep-safe-chain"]      = false,
            ["cycle-with-target"]    = true,
            ["cycle-safe"]           = false,
            ["byref-out-target"]     = true,
            ["byref-ref-target"]     = true,
            ["byref-return-target"]  = true,
            ["byref-in-target"]      = false,
            ["byvalue-input-target"] = false,
        };

        foreach (var (label, mustBeRejected) in expectedSide)
        {
            var row = Assert.Single(rows, r => (string)r[2] == label);
            Assert.Equal(mustBeRejected, (bool)row[1]);
        }

        // 3. the accepted earlier cases are still present on their accepted sides.
        foreach (string retained in new[]
                 {
                     "R1 renamed direct factory", "R2 payload factory",
                     "R3 source-declared implicit conversion", "R4 source-declared explicit conversion",
                     "R5 direct static property", "R6 direct static field",
                     "R7 non-generic carrier", "R8 Task<T> output wrapper", "R9 array exposure",
                 })
            Assert.Single(rows, r => (string)r[2] == retained && (bool)r[1]);

        foreach (string retained in new[]
                 {
                     "A1 unrelated static", "A2 object return", "A3 base Exception return",
                     "A4 unconstrained generic return", "A5 harmless public nested enum",
                     "A5 the nested enum itself", "A6 input-only use of the target",
                 })
            Assert.Single(rows, r => (string)r[2] == retained && !(bool)r[1]);

        // 4. size, stated last so a mismatch reports the interesting failure first.
        Assert.Equal(25, rows.Length);
        Assert.Equal(14, rows.Count(r => (bool)r[1]));
        Assert.Equal(11, rows.Count(r => !(bool)r[1]));
    }

    [Fact]
    public void Analyser_TerminatesAndVisitsASmallGraphForRealProductionTypes()
    {
        // Termination and cost are observable, not assumed: the whole production static surface is
        // analysed and the test completes normally. Removing the depth cutoff did not make the walk
        // pathological, because the visited set already bounds it.
        var exported = Target.Assembly.GetExportedTypes();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var offenders = ScanForTargetProducingSurface(exported);
        stopwatch.Stop();

        Assert.Empty(offenders);
        Assert.True(stopwatch.ElapsedMilliseconds < 10_000,
            $"production output-graph scan took {stopwatch.ElapsedMilliseconds} ms");

        // A deliberately deep chain also terminates well inside the budget.
        Assert.True(ExposesTarget(typeof(Deep1)));
        Assert.False(ExposesTarget(typeof(Safe1)));
        Assert.False(ExposesTarget(typeof(CycleA)));
        Assert.True(ExposesTarget(typeof(CycleTargetA)));
    }

    [Fact]
    public void DetectorRankMismatchException_KeepsExactlyTheThreeContractedReadOnlyObservables()
    {
        // §16.3.1: "exposes exactly Id, ExpectedRank and ActualRank; no fourth observable field
        // exists, so no third key component is required -- and none may be added." A fourth public
        // observable would silently widen the rule-2b selector's obligation.
        var type = typeof(DetectorRankMismatchException);

        var declared = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal(3, declared.Length);
        Assert.Equal(
            new[] { "ActualRank", "ExpectedRank", "Id" },
            declared.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // Get-only: no setter, no init, so ExpectedRank cannot be rewritten after construction.
        foreach (var property in declared)
        {
            Assert.True(property.CanRead, property.Name);
            Assert.False(property.CanWrite, property.Name + " must be get-only.");
        }

        // No public field -- instance or static -- can carry or expose a forged rank.
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));

        // No public static property may expose mutable or fabricated observable state either.
        Assert.Empty(type.GetProperties(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void RegistryThrownPayload_IsUnchangedByTheConstructorVisibilityFix()
    {
        // The visibility change must alter NOTHING the registry observably produces. This re-asserts
        // the accepted critical example end to end, through the real production seam.
        var ex = Assert.Throws<DetectorRankMismatchException>(
            () => DetectorRegistry.Create([Probe("kr.phone", 301)]));

        Assert.Equal("kr.phone", ex.Id.Value);
        Assert.Equal(300, ex.ExpectedRank);
        Assert.Equal(301, ex.ActualRank);
        Assert.Equal("DetectorRankMismatch(kr.phone, expected=300, actual=301)", ex.Message);

        // ExpectedRank is the table's value, never anything the supplied detector reported.
        Assert.NotEqual(ex.ActualRank, ex.ExpectedRank);
    }

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (int i = 0; i < items.Length; i++)
        {
            var rest = items.Take(i).Concat(items.Skip(i + 1)).ToArray();
            foreach (var tail in Permutations(rest))
                yield return new[] { items[i] }.Concat(tail).ToArray();
        }
    }
}
