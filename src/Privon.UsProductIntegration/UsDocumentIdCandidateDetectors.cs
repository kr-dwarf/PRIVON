using Privon.Detection;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// IDetector adapters for us.passport, us.a_number and us.i94. Identity, rank and readiness are literals, never inferred
// (DETECTOR_IDENTITY_IS_EXPLICIT); readiness is Ready because none of the three depends on an authority snapshot. A failure of
// a core is never caught: a failed scan must not look like "no candidates".

internal static class UsDocumentDetection
{
    public static IReadOnlyList<DetectionCandidate> Convert(
        IReadOnlyList<UsDocumentMatch> matches, PiiType type, DetectorId id, int rank, string detectorName)
    {
        var candidates = new List<DetectionCandidate>(matches.Count);
        foreach (var m in matches)
        {
            candidates.Add(UsCandidateFactory.Create(
                type, id, rank, detectorName, m.Span, m.Canonical, UsCandidateFactory.EvidenceOf(m.Evidence), m.OwnerClaim,
                m.Level, m.Confidence));
        }

        return candidates.AsReadOnly();
    }
}

/// <summary>
/// us.phone (rank 310, Level 2). Depends on the NPA authority snapshot: without a VALID one its readiness is exactly
/// <see cref="DetectorReadinessState.UnavailableAuthoritySnapshot"/> and it emits ZERO candidates (no all-NANP fallback).
/// </summary>
public sealed class UsPhoneCandidateDetector : IDetector
{
    private readonly UsAuthoritySet _authority;

    public UsPhoneCandidateDetector() : this(UsAuthoritySet.Unavailable) { }

    public UsPhoneCandidateDetector(UsAuthoritySet authority) =>
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));

    public string Name => "UsPhoneCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.Phone;

    public DetectorId Id { get; } = DetectorId.Create("us.phone");

    public int ResolutionRank => 310;

    public DetectorReadinessState Readiness =>
        _authority.PhoneReady ? DetectorReadinessState.Ready : DetectorReadinessState.UnavailableAuthoritySnapshot;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _authority.PhoneReady
            ? UsDocumentDetection.Convert(UsPhoneCore.Detect(context.View.RawText, _authority), PiiType, Id, ResolutionRank, Name)
            : [];
    }
}

/// <summary>
/// us.address (rank 330, Level 2). Depends on the USPS suffix AND state snapshots: if either is not VALID its readiness is
/// <see cref="DetectorReadinessState.UnavailableAuthoritySnapshot"/> and it emits ZERO candidates.
/// </summary>
public sealed class UsAddressCandidateDetector : IDetector
{
    private readonly UsAuthoritySet _authority;

    public UsAddressCandidateDetector() : this(UsAuthoritySet.Unavailable) { }

    public UsAddressCandidateDetector(UsAuthoritySet authority) =>
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));

    public string Name => "UsAddressCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.Address;

    public DetectorId Id { get; } = DetectorId.Create("us.address");

    public int ResolutionRank => 330;

    public DetectorReadinessState Readiness =>
        _authority.AddressReady ? DetectorReadinessState.Ready : DetectorReadinessState.UnavailableAuthoritySnapshot;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _authority.AddressReady
            ? UsDocumentDetection.Convert(UsAddressCore.Detect(context.View.RawText, _authority), PiiType, Id, ResolutionRank, Name)
            : [];
    }
}

/// <summary>us.name (rank 350, Level 2 for the specific labels, Level 1 for the generic label and the honorific forms).</summary>
public sealed class UsNameCandidateDetector : IDetector
{
    public string Name => "UsNameCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.Name;

    public DetectorId Id { get; } = DetectorId.Create("us.name");

    public int ResolutionRank => 350;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return UsDocumentDetection.Convert(UsNameCore.Detect(context.View.RawText), PiiType, Id, ResolutionRank, Name);
    }
}

public sealed class UsDriverStateIdCandidateDetector : IDetector
{
    public string Name => "UsDriverStateIdCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.UsDriverStateId;

    public DetectorId Id { get; } = DetectorId.Create("us.driver_state_id");

    public int ResolutionRank => 130;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return UsDocumentDetection.Convert(UsDriverStateIdCore.Detect(context.View.RawText), PiiType, Id, ResolutionRank, Name);
    }
}

public sealed class UsBankAccountCandidateDetector : IDetector
{
    public string Name => "UsBankAccountCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.BankAccount;

    public DetectorId Id { get; } = DetectorId.Create("us.bank_account");

    public int ResolutionRank => 140;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return UsDocumentDetection.Convert(UsBankAccountCore.Detect(context.View.RawText), PiiType, Id, ResolutionRank, Name);
    }
}

public sealed class UsPassportCandidateDetector : IDetector
{
    public string Name => "UsPassportCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.UsPassport;

    public DetectorId Id { get; } = DetectorId.Create("us.passport");

    public int ResolutionRank => 100;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return UsDocumentDetection.Convert(UsPassportCore.Detect(context.View.RawText), PiiType, Id, ResolutionRank, Name);
    }
}

public sealed class UsANumberCandidateDetector : IDetector
{
    public string Name => "UsANumberCandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.UsANumber;

    public DetectorId Id { get; } = DetectorId.Create("us.a_number");

    public int ResolutionRank => 110;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return UsDocumentDetection.Convert(UsANumberCore.Detect(context.View.RawText), PiiType, Id, ResolutionRank, Name);
    }
}

public sealed class UsI94CandidateDetector : IDetector
{
    public string Name => "UsI94CandidateDetector";

    public PiiType PiiType => global::Privon.Detection.PiiType.UsI94;

    public DetectorId Id { get; } = DetectorId.Create("us.i94");

    public int ResolutionRank => 120;

    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return UsDocumentDetection.Convert(UsI94Core.Detect(context.View.RawText), PiiType, Id, ResolutionRank, Name);
    }
}
