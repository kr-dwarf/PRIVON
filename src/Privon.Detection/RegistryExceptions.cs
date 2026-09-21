namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §16.5. UNKNOWN_ID_IS_NOT_NO_CANDIDATE: no production or test path may translate
/// this into NoCandidate, NoUsCandidate, ExpectedCandidates=[], or a pass.
/// OUTSIDE the resolver failure union (§15) -- the union stays 3 / 5 / 4.
/// </summary>
public sealed class UnknownDetectorIdException : Exception
{
    public UnknownDetectorIdException(DetectorId id)
        : base("UnknownDetectorId(" + id.ToString() + ")") => Id = id;

    public DetectorId Id { get; }
}

/// <summary>D3B0-A A.9 §16.3 rule 3 -- never deduped, never repaired. Outside the resolver union.</summary>
public sealed class DuplicateDetectorIdException : Exception
{
    public DuplicateDetectorIdException(DetectorId id)
        : base("DuplicateDetectorId(" + id.ToString() + ")") => Id = id;

    public DetectorId Id { get; }
}

/// <summary>D3B0-A A.9 §16.3 rule 4 -- never deduped, never reordered. Outside the resolver union.</summary>
public sealed class DuplicateDetectorRankException : Exception
{
    public DuplicateDetectorRankException(int rank)
        : base("DuplicateDetectorRank(" + rank.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")")
        => Rank = rank;

    public int Rank { get; }
}

/// <summary>
/// D3B0-A A.15 §16.3.1 rule 2b -- a detector carrying a FROZEN DetectorId paired with any rank
/// other than the one the frozen D2.2 table assigns that id.
///
/// This is the ONE type A.12 added, and it exists because neither existing type can carry the
/// failure truthfully: reporting a known id through <see cref="UnknownDetectorIdException"/> would
/// assert the id is unknown when it is not, and reporting a non-duplicate through
/// <see cref="DuplicateDetectorRankException"/> would assert a duplicate that does not exist.
/// §26 must be able to assert WHICH invariant failed, so the observable may not lie about it.
///
/// Exposes exactly Id, ExpectedRank and ActualRank -- no fourth observable field exists, so no
/// third rule-2b selector key component is required and none may be added
/// (RULE_2B_OFFENDER_SELECTOR_IS_TOTAL, §16.3.1). ExpectedRank is always T(Id), derived from the
/// frozen table and never caller-controlled.
///
/// Outside the resolver failure union (§15) -- the union stays 3 / 5 / 4.
///
/// NO_EXTERNAL_RANK_MISMATCH_CONSTRUCTION. The TYPE is public, because §23.3 declares it public and
/// tests must be able to catch it and read its observables. The CONSTRUCTOR is internal, because
/// §16.3.1 requires that ExpectedRank = T(Id) and is "never caller-controlled". A public constructor
/// taking expectedRank let arbitrary external code build an instance whose ExpectedRank != T(Id) --
/// an exception that lies about which rank the frozen table authorizes. That is a public-surface
/// defect, not a usage question, so it is closed structurally rather than by documentation.
///
/// Privon.Detection declares NO InternalsVisibleTo, so the constructor is unreachable from every
/// other assembly INCLUDING the test assembly: no test can fabricate a false ExpectedRank either.
/// DetectorRegistry.Create is the only construction site in the codebase and derives ExpectedRank
/// from the single frozen table as T(Id).
///
/// The frozen table is deliberately NOT exposed here and NOT duplicated here: a second mapping
/// could drift from the registry's. The registry owns T and supplies the derived value.
/// </summary>
public sealed class DetectorRankMismatchException : Exception
{
    internal DetectorRankMismatchException(DetectorId id, int expectedRank, int actualRank)
        : base("DetectorRankMismatch("
               + id.ToString()
               + ", expected="
               + expectedRank.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + ", actual="
               + actualRank.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + ")")
    {
        Id = id;
        ExpectedRank = expectedRank;
        ActualRank = actualRank;
    }

    public DetectorId Id { get; }

    /// <summary>The frozen rank the D2.2 table assigns <see cref="Id"/>. Never caller-supplied.</summary>
    public int ExpectedRank { get; }

    /// <summary>The wrong rank the detector actually reported.</summary>
    public int ActualRank { get; }
}

/// <summary>D3B0-A A.9 §16.3 rule 5 -- an undefined readiness value fails closed. Outside the resolver union.</summary>
public sealed class InvalidDetectorReadinessException : Exception
{
    public InvalidDetectorReadinessException(DetectorId id)
        : base("InvalidDetectorReadiness(" + id.ToString() + ")") => Id = id;

    public DetectorId Id { get; }
}
