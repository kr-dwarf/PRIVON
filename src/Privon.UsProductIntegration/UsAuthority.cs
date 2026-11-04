using Privon.Detection.Authority;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// The external authority data the U.S. phone and address detectors depend on (D2.1 authority-snapshot provenance): the NPA
// list, the USPS street-suffix list and the USPS state / possession list. Every snapshot goes through the product's ONE
// AuthoritySnapshotLoader (source URL, retrieval date, record count, SHA-256, version are verified there), so a missing,
// unverifiable, count-mismatched or tampered snapshot makes the depending detector UnavailableAuthoritySnapshot with ZERO
// candidates -- never an all-NANP list, a built-in default list, a cached list or a partial record set.
//
// TWO WAYS TO LOAD, deliberately different:
//   * Load(provider)            -- shape-only. Accepts any self-consistent snapshot a caller explicitly provides (the four-entry D3A
//                                  test fixtures, a partial list, ...). Used by tests. Its result is NEVER production data.
//   * LoadProduction(provider)  -- shape AND scope. Additionally requires that the data covers every state, DC and territory (NPA, with
//                                  its jurisdiction on every record), every state / possession code the U.S. pack must recognise, and the
//                                  core USPS street suffixes. A self-consistent but too-small snapshot (the D3A fixtures) is rejected here.
// UsAuthoritySet.Production loads the artifact embedded in this assembly (AuthorityData/, built from public NANPA and USPS files read once)
// through LoadProduction. Nothing is ever fetched at run time.

/// <summary>Where a loaded detector dependency came from: nothing, a shape-checked provider, or a scope-checked production artifact.</summary>
public enum UsAuthorityOrigin
{
    None = 0,

    /// <summary>Loaded and shape-checked from a caller-supplied provider. Not production data.</summary>
    Provided = 1,

    /// <summary>Loaded and checked against the full production scope.</summary>
    ProductionScope = 2,
}

/// <summary>The three validated authority snapshots (or the reason each one is unavailable). Holds no personal data.</summary>
public sealed class UsAuthoritySet
{
    /// <summary>Snapshot ids the provider is asked for. Neutral names: a D3 test fixture and a production list differ only in data.</summary>
    public static AuthoritySnapshotId NpaId { get; } = Id("us.npa");

    public static AuthoritySnapshotId SuffixId { get; } = Id("usps.suffix");

    public static AuthoritySnapshotId StateId { get; } = Id("usps.state");

    // 50 states + DC + the five territories NANPA assigns to the United States (NANPA writes the Northern Mariana Islands as CNMI).
    private static readonly string[] RequiredNpaJurisdictions =
    [
        "AK", "AL", "AR", "AZ", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "IA", "ID", "IL", "IN", "KS", "KY", "LA", "MA", "MD", "ME", "MI",
        "MN", "MO", "MS", "MT", "NC", "ND", "NE", "NH", "NJ", "NM", "NV", "NY", "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT",
        "VA", "VT", "WA", "WI", "WV", "WY", "DC", "AS", "CNMI", "GU", "PR", "VI",
    ];

    // The same states, DC and territories as USPS Publication 28 Appendix B writes them (MP for the Northern Mariana Islands). The
    // table also lists FM, MH and PW; they are allowed but not required.
    private static readonly string[] RequiredStateCodes =
    [
        "AK", "AL", "AR", "AZ", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "IA", "ID", "IL", "IN", "KS", "KY", "LA", "MA", "MD", "ME", "MI",
        "MN", "MO", "MS", "MT", "NC", "ND", "NE", "NH", "NJ", "NM", "NV", "NY", "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT",
        "VA", "VT", "WA", "WI", "WV", "WY", "DC", "AS", "GU", "MP", "PR", "VI",
    ];

    // Listed in the same Appendix B table as possessions (the freely associated states USPS addresses with U.S. ZIP codes): allowed, not required.
    private static readonly string[] AllowedExtraStateCodes = ["FM", "MH", "PW"];

    // Postal Service standard abbreviations (Pub 28 C1, third column) that any real street-suffix list must contain.
    private static readonly string[] RequiredSuffixAnchors =
    [
        "ALY", "AVE", "BLVD", "BND", "BR", "BYP", "CIR", "CT", "CV", "DR", "EXPY", "FWY", "HWY", "LN", "LOOP", "PKWY", "PL", "PLZ", "RD",
        "ROW", "RUN", "SQ", "ST", "TER", "TRL", "WAY", "XING",
    ];

    private static readonly Lazy<UsAuthoritySet> ProductionSet = new(
        () => LoadProduction(new UsProductionAuthorityProvider()), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly HashSet<string>? _npa;
    private readonly HashSet<string>? _suffix;
    private readonly HashSet<string>? _state;

    private UsAuthoritySet(
        HashSet<string>? npa, HashSet<string>? suffix, HashSet<string>? state,
        AuthoritySnapshotState npaState, AuthoritySnapshotState suffixState, AuthoritySnapshotState stateState,
        UsAuthorityOrigin origin, IReadOnlyList<string> scopeIssues)
    {
        _npa = npa;
        _suffix = suffix;
        _state = state;
        NpaState = npaState;
        SuffixState = suffixState;
        StateState = stateState;
        Origin = origin;
        ScopeIssues = scopeIssues;
    }

    /// <summary>No provider, no data: phone and address are not ready.</summary>
    public static UsAuthoritySet Unavailable { get; } = new(
        null, null, null,
        AuthoritySnapshotState.SnapshotMissing, AuthoritySnapshotState.SnapshotMissing, AuthoritySnapshotState.SnapshotMissing,
        UsAuthorityOrigin.None, []);

    /// <summary>
    /// The official-source artifact embedded in this assembly, loaded through the production loader AND the production scope check. It
    /// is not picked up implicitly by anything: a caller that wants the phone and address detectors ready passes this explicitly.
    /// </summary>
    public static UsAuthoritySet Production => ProductionSet.Value;

    public AuthoritySnapshotState NpaState { get; }

    public AuthoritySnapshotState SuffixState { get; }

    public AuthoritySnapshotState StateState { get; }

    public UsAuthorityOrigin Origin { get; }

    /// <summary>Why a valid snapshot was still not accepted at production scope (names of missing jurisdictions / codes / anchors only).</summary>
    public IReadOnlyList<string> ScopeIssues { get; }

    public bool PhoneReady => _npa is not null;

    public bool AddressReady => _suffix is not null && _state is not null;

    /// <summary>Loads and shape-checks the three snapshots through the production loader. A failure is recorded, never repaired.</summary>
    public static UsAuthoritySet Load(IAuthoritySnapshotProvider provider) => Load(provider, productionScope: false);

    /// <summary>
    /// Like <see cref="Load(IAuthoritySnapshotProvider)"/>, and additionally requires the production scope for each snapshot. A snapshot
    /// that fails the scope check is not used at all: the detector that depends on it stays unavailable.
    /// </summary>
    public static UsAuthoritySet LoadProduction(IAuthoritySnapshotProvider provider) => Load(provider, productionScope: true);

    private static UsAuthoritySet Load(IAuthoritySnapshotProvider provider, bool productionScope)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var issues = new List<string>();
        var npa = LoadOne(provider, NpaId, NpaShape(productionScope), out var npaState);
        var suffix = LoadOne(provider, SuffixId, IsSuffix, out var suffixState);
        var state = LoadOne(provider, StateId, IsStateCode, out var stateState);

        HashSet<string>? npaNumbers = null;
        if (npa is not null)
        {
            npaNumbers = npa.Select(r => r[..3]).ToHashSet(StringComparer.Ordinal);
            if (npaNumbers.Count != npa.Count)
            {
                npaNumbers = null;  // one NPA listed twice (even for different jurisdictions) is contradictory data, not a list
                npaState = AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord;
            }
            else if (productionScope)
            {
                var present = npa.Select(r => r[4..]).ToHashSet(StringComparer.Ordinal);
                Missing("us.npa jurisdiction", RequiredNpaJurisdictions, present, issues);
                // NPA OWNERSHIP: a code assigned to anything but the United States and its territories (Canada, the Caribbean, ...) is
                // not a U.S. phone area code; one such record makes the whole snapshot unfit, never a quietly broader list.
                Unexpected("us.npa jurisdiction", present, RequiredNpaJurisdictions, issues);
            }
        }

        if (productionScope && suffix is not null)
        {
            Missing("usps.suffix anchor", RequiredSuffixAnchors, suffix, issues);
        }

        if (productionScope && state is not null)
        {
            Missing("usps.state code", RequiredStateCodes, state, issues);
            Unexpected("usps.state code", state, RequiredStateCodes.Concat(AllowedExtraStateCodes), issues);
        }

        // A snapshot that is valid but short of the production scope is dropped, never half-used.
        if (issues.Any(i => i.StartsWith("us.npa", StringComparison.Ordinal))) npaNumbers = null;
        if (issues.Any(i => i.StartsWith("usps.suffix", StringComparison.Ordinal))) suffix = null;
        if (issues.Any(i => i.StartsWith("usps.state", StringComparison.Ordinal))) state = null;

        return new UsAuthoritySet(
            npaNumbers, suffix, state, npaState, suffixState, stateState,
            productionScope ? UsAuthorityOrigin.ProductionScope : UsAuthorityOrigin.Provided, issues.AsReadOnly());
    }

    internal bool IsKnownNpa(ReadOnlySpan<char> npa) => _npa is not null && _npa.Contains(npa.ToString());

    internal bool IsKnownSuffix(ReadOnlySpan<char> token) => _suffix is not null && _suffix.Contains(UsAscii.Upper(token));

    internal bool IsKnownState(ReadOnlySpan<char> code) => _state is not null && _state.Contains(code.ToString());

    private static void Unexpected(string what, HashSet<string> present, IEnumerable<string> allowed, List<string> issues)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (string item in present.Order(StringComparer.Ordinal))
        {
            if (!allowedSet.Contains(item))
            {
                issues.Add($"{what} not allowed: {item}");
            }
        }
    }

    private static void Missing(string what, IEnumerable<string> required, HashSet<string> present, List<string> issues)
    {
        foreach (string item in required)
        {
            if (!present.Contains(item))
            {
                issues.Add($"{what}: {item}");
            }
        }
    }

    private static HashSet<string>? LoadOne(
        IAuthoritySnapshotProvider provider, AuthoritySnapshotId id, Func<string, bool> recordIsValid, out AuthoritySnapshotState state)
    {
        if (!AuthoritySnapshotLoader.TryLoad(provider, id, out var snapshot, out var validation))
        {
            state = validation.State;
            return null;
        }

        // The generic loader checks bytes, metadata, version, hash, duplicates and count. The record SHAPE is this detector's
        // own knowledge: a record the detector could not use means the snapshot cannot be trusted as a whole.
        foreach (string record in snapshot.NormalizedRecords)
        {
            if (!recordIsValid(record))
            {
                state = AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord;
                return null;
            }
        }

        state = AuthoritySnapshotState.SnapshotValid;
        return new HashSet<string>(snapshot.NormalizedRecords, StringComparer.Ordinal);
    }

    // Shape-only: "212" per record. Production: "212 NY" per record (the jurisdiction travels with the number, so the scope can be checked).
    private static Func<string, bool> NpaShape(bool productionScope) => productionScope
        ? record => record.Length >= 6 && record.Length <= 8 && IsNpa(record[..3]) && record[3] == ' ' && IsJurisdiction(record[4..])
        : record => IsNpa(record) || (record.Length >= 6 && IsNpa(record[..3]) && record[3] == ' ' && IsJurisdiction(record[4..]));

    private static bool IsNpa(string record) =>
        record.Length == 3 && record[0] >= '2' && record[0] <= '9' && UsAscii.IsDigit(record[1]) && UsAscii.IsDigit(record[2]);

    private static bool IsJurisdiction(string value) => value.Length is >= 2 and <= 4 && value.All(c => c >= 'A' && c <= 'Z');

    private static bool IsSuffix(string record) => record.Length >= 2 && record.All(c => c >= 'A' && c <= 'Z');

    private static bool IsStateCode(string record) => record.Length == 2 && record.All(c => c >= 'A' && c <= 'Z');

    private static AuthoritySnapshotId Id(string value)
    {
        AuthoritySnapshotId.TryCreate(value, out var id);
        return id;
    }
}
