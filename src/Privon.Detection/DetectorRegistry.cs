namespace Privon.Detection;

/// <summary>
/// D3B0-A A.15 §16 -- stable registration and lookup over a CLOSED frozen identity domain.
///
/// Registration NEVER repairs, dedupes, reorders, or drops. Lookup is exact, ordinal and
/// case-sensitive on <see cref="DetectorId.Value"/>: no prefix matching, no normalization, no
/// PiiType fallback, no Name fallback, no reflection
/// (DETECTOR_NAME_IS_NOT_AN_IDENTITY, §6).
///
/// <see cref="All"/> is ordered by ResolutionRank ascending -- independent of supply order and
/// NEVER a resolver tiebreaker (FORBIDDEN_HIDDEN_TIEBREAKERS, §9.2).
/// </summary>
public sealed class DetectorRegistry
{
    /// <summary>
    /// FROZEN_ID_RANK_MAPPING_IS_TOTAL_AND_CLOSED (§16.3.1) -- the registrable identity domain IS
    /// the frozen D2.2 table. This is the production copy of <c>T</c>, the tier-2 authority the
    /// contract quotes but never edits: 26 detector IDs, 26 distinct ranks, injective.
    ///
    /// It exists so that rule 2 can be ENFORCED rather than assumed. Before A.15's remediation the
    /// registry held no copy and merely trusted each detector's own literal, so an arbitrary
    /// ID/rank pair could register and frozen numeric precedence could drift -- the exact P1 the
    /// independent implementation audit raised.
    ///
    /// This is registry AUTHORITY METADATA ONLY. Holding a rank here neither implements nor
    /// activates a detector: NO_DETECTOR_ACTIVATION_BY_BRIDGE (§16.6) is unaffected, and the 17
    /// identities with no implementation simply have no detector that can satisfy rule 2b.
    /// </summary>
    private static readonly Dictionary<string, int> FrozenRankTable = new(StringComparer.Ordinal)
    {
        ["global.secret"]      =  10,
        ["global.card"]        =  20,
        ["kr.rrn"]             =  30,
        ["kr.bank_account"]    =  40,
        ["us.atin"]            =  50,
        ["us.itin"]            =  60,
        ["us.ssn"]             =  70,
        ["us.mbi"]             =  80,
        ["us.sevis"]           =  90,
        ["us.passport"]        = 100,
        ["us.a_number"]        = 110,
        ["us.i94"]             = 120,
        ["us.driver_state_id"] = 130,
        ["us.bank_account"]    = 140,
        ["global.email"]       = 200,
        ["global.ip"]          = 210,
        ["global.mac"]         = 220,
        ["global.gps"]         = 230,
        ["global.path"]        = 240,
        ["kr.phone"]           = 300,
        ["us.phone"]           = 310,
        ["kr.address"]         = 320,
        ["us.address"]         = 330,
        ["kr.name"]            = 340,
        ["us.name"]            = 350,
        ["kr.company"]         = 360,
    };

    private readonly Dictionary<string, DetectorRegistration> _byId;
    private readonly DetectorRegistration[] _all;

    private DetectorRegistry(Dictionary<string, DetectorRegistration> byId, DetectorRegistration[] all)
    {
        _byId = byId;
        _all = all;
    }

    /// <summary>
    /// §16.3 / §16.3.1 validation, fail-closed and in the exact accepted class-major order.
    ///
    /// Each rule is evaluated over EVERY supplied detector before the next rule begins -- the same
    /// discipline as R0.5 (§11.2), §7.3 and §21.3.4 -- so supply order can never decide which
    /// failure is reported. A "throw on the first enumeration hit" implementation would make the
    /// observable exception depend on caller order, which the contract forbids.
    ///
    ///   1  every Id is a grammatically valid DetectorId  -> ArgumentException(paramName "detectors")
    ///   2a every Id is a MEMBER of the frozen table      -> UnknownDetectorIdException(id)
    ///   2b every rank equals T(Id)                       -> DetectorRankMismatchException(id, expected, actual)
    ///   3  no duplicate DetectorId                       -> DuplicateDetectorIdException(id)
    ///   4  no duplicate ResolutionRank                   -> DuplicateDetectorRankException(rank)
    ///   5  every Readiness is a DEFINED member           -> InvalidDetectorReadinessException(id)
    ///   6  Id and ResolutionRank read once and snapshotted
    ///
    /// Rule 4 is STRUCTURALLY UNREACHABLE under the current injective table
    /// (DUPLICATE_RANK_IS_UNREACHABLE_UNDER_THE_CURRENT_TABLE, §16.3.2): after rule 2b every rank
    /// is T(Id), so two equal ranks force two equal Ids, which rule 3 already rejected. It is kept
    /// as declared defence-in-depth because the proof is conditional on the CURRENT table staying
    /// injective; an amended D2.2 table that permits equal ranks makes it reachable with no code
    /// change. It is never reordered or bypassed to make it observable.
    /// </summary>
    public static DetectorRegistry Create(IReadOnlyList<IDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);

        // (6) read Id and ResolutionRank EXACTLY ONCE, then work only from the snapshot, so a
        // detector cannot report one identity to validation and another to registration.
        var entries = new List<Entry>(detectors.Count);
        foreach (var detector in detectors)
        {
            ArgumentNullException.ThrowIfNull(detector);
            entries.Add(new Entry(detector.Id, detector.ResolutionRank, detector));
        }

        // ---- rule 1: grammatically valid identity, class-major --------------------------------
        // default(DetectorId) is never a lookup key. No offender is exposed: the accepted payload
        // for this class is the ArgumentException and its ParamName alone.
        foreach (var e in entries)
        {
            if (!e.Id.HasValue || !DetectorId.TryCreate(e.Id.Value, out _))
                throw new ArgumentException(
                    "Detector supplied an invalid DetectorId.", nameof(detectors));
        }

        // ---- rule 2a: the identity domain is CLOSED, class-major ------------------------------
        // An Id outside the frozen table has NO authorized rank, so it cannot be validated at all
        // and MUST be rejected. Canonical offender: smallest DetectorId.Value in UTF-8 byte order
        // (§12.2) -- the payload is a function of the Id alone, so one key component is total.
        Entry? unknown = null;
        foreach (var e in entries)
        {
            if (FrozenRankTable.ContainsKey(e.Id.Value)) continue;
            if (unknown is null || Utf8ByteOrder.Compare(e.Id.Value, unknown.Value.Id.Value) < 0)
                unknown = e;
        }
        if (unknown is not null) throw new UnknownDetectorIdException(unknown.Value.Id);

        // ---- rule 2b: rank must equal T(Id), class-major --------------------------------------
        // RULE_2B_OFFENDER_SELECTOR_IS_TOTAL (§16.3.1, A13-P1-1): the payload also carries
        // ActualRank, so selecting on Id alone TIES when one known Id is supplied twice with two
        // different wrong ranks. The canonical offender is the unique minimum under the
        // lexicographic key ( DetectorId.Value UTF-8 byte asc , ActualRank int asc ).
        //
        // ExpectedRank is deliberately NOT a key: it is T(Id), a function of key component 1, and
        // is never caller-controlled. The exception exposes no fourth observable, so no third key
        // component exists and none may be added.
        Entry? mismatch = null;
        foreach (var e in entries)
        {
            int expected = FrozenRankTable[e.Id.Value];
            if (e.Rank == expected) continue;
            if (mismatch is null || CompareRule2bKey(e, mismatch.Value) < 0)
                mismatch = e;
        }
        if (mismatch is not null)
        {
            var m = mismatch.Value;
            throw new DetectorRankMismatchException(m.Id, FrozenRankTable[m.Id.Value], m.Rank);
        }

        // ---- rule 3: no duplicate DetectorId, class-major -------------------------------------
        // Never deduped. Canonical offender: smallest DUPLICATED Id in UTF-8 byte order.
        DetectorId? duplicateId = null;
        var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in entries)
            idCounts[e.Id.Value] = idCounts.TryGetValue(e.Id.Value, out int n) ? n + 1 : 1;
        foreach (var e in entries)
        {
            if (idCounts[e.Id.Value] < 2) continue;
            if (duplicateId is null || Utf8ByteOrder.Compare(e.Id.Value, duplicateId.Value.Value) < 0)
                duplicateId = e.Id;
        }
        if (duplicateId is not null) throw new DuplicateDetectorIdException(duplicateId.Value);

        // ---- rule 4: no duplicate ResolutionRank, class-major ---------------------------------
        // STRUCTURALLY UNREACHABLE here (§16.3.2). Retained verbatim as declared defence-in-depth
        // and evaluated in its accepted position; never reordered to make it observable.
        // Canonical offender: smallest DUPLICATED rank.
        int? duplicateRank = null;
        var rankCounts = new Dictionary<int, int>();
        foreach (var e in entries)
            rankCounts[e.Rank] = rankCounts.TryGetValue(e.Rank, out int n) ? n + 1 : 1;
        foreach (var e in entries)
        {
            if (rankCounts[e.Rank] < 2) continue;
            if (duplicateRank is null || e.Rank < duplicateRank.Value) duplicateRank = e.Rank;
        }
        if (duplicateRank is not null) throw new DuplicateDetectorRankException(duplicateRank.Value);

        // ---- rule 5: readiness must be a DEFINED member, class-major --------------------------
        // An undefined cast fails closed. Canonical offender: smallest Id in UTF-8 byte order.
        Entry? badReadiness = null;
        foreach (var e in entries)
        {
            var readiness = e.Detector.Readiness;
            if (readiness is DetectorReadinessState.Ready
                          or DetectorReadinessState.UnavailableAuthoritySnapshot) continue;
            if (badReadiness is null || Utf8ByteOrder.Compare(e.Id.Value, badReadiness.Value.Id.Value) < 0)
                badReadiness = e;
        }
        if (badReadiness is not null) throw new InvalidDetectorReadinessException(badReadiness.Value.Id);

        // ---- commit ---------------------------------------------------------------------------
        var byId = new Dictionary<string, DetectorRegistration>(StringComparer.Ordinal);
        var all = new List<DetectorRegistration>(entries.Count);
        foreach (var e in entries)
        {
            var registration = new DetectorRegistration(e.Id, e.Detector, e.Rank);
            byId.Add(e.Id.Value, registration);
            all.Add(registration);
        }

        // §16.4 -- rank ascending, total under rule 4, independent of supply order.
        all.Sort(static (a, b) => a.ResolutionRank.CompareTo(b.ResolutionRank));
        return new DetectorRegistry(byId, [.. all]);
    }

    /// <summary>
    /// The rule-2b lexicographic key: ( DetectorId.Value UTF-8 byte asc , ActualRank int asc ).
    /// Both components are total orders, so the product order is total on distinct pairs and a
    /// finite non-empty failure set has exactly one minimum. Entries sharing that pair are
    /// observationally identical -- equal Id and equal ActualRank force equal ExpectedRank -- so
    /// the emitted payload is byte-identical whichever is nominated.
    /// </summary>
    private static int CompareRule2bKey(in Entry a, in Entry b)
    {
        int byId = Utf8ByteOrder.Compare(a.Id.Value, b.Id.Value);
        return byId != 0 ? byId : a.Rank.CompareTo(b.Rank);
    }

    private readonly record struct Entry(DetectorId Id, int Rank, IDetector Detector);

    /// <summary>Exact ordinal match. Returns false for an unregistered id -- never a fallback.</summary>
    public bool TryGet(DetectorId id, out DetectorRegistration registration)
    {
        if (id.HasValue && _byId.TryGetValue(id.Value, out var found))
        {
            registration = found;
            return true;
        }
        registration = null!;
        return false;
    }

    /// <summary>
    /// §16.5 fail-closed: throws for an unregistered id. NEVER returns null, an empty detector,
    /// a no-op detector, a sentinel, or an empty candidate list.
    /// </summary>
    public DetectorRegistration Get(DetectorId id) =>
        TryGet(id, out var registration) ? registration : throw new UnknownDetectorIdException(id);

    /// <summary>Rank ascending; never a resolver tiebreaker.</summary>
    public IReadOnlyList<DetectorRegistration> All => _all;

    public int GetRank(DetectorId id) => Get(id).ResolutionRank;
}
