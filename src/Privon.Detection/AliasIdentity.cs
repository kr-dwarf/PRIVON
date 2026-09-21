using System.Globalization;

namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §22.2 -- the neutral, language-independent alias identity.
///
/// Equality, hash code and CandidateSemanticIdentity participation use ONLY
/// <see cref="Family"/> and <see cref="Occurrence"/> -- the record's own synthesized equality,
/// which reads exactly those two members. <see cref="CanonicalContractToken"/> is derived, never
/// stored, never compared directly for identity.
/// </summary>
public sealed record AliasIdentity(AliasFamilyId Family, int Occurrence)
{
    /// <summary>
    /// §22.4: <c>"[" + FrozenTokenStem(Family) + Occurrence + "]"</c>, where Occurrence renders
    /// as invariant-culture decimal with no grouping separators, no padding, and no sign.
    ///
    /// Derived ONLY from the neutral identity: no renderer lookup, no ambient locale, no
    /// CultureInfo.CurrentCulture, no mutable setting, no display text. Independent of UI
    /// language and of Replacement Language.
    /// </summary>
    public string CanonicalContractToken =>
        "[" + AliasFamilyTable.FrozenTokenStem(Family)
            + Occurrence.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>§22.6 -- exactly the canonical contract token; one interpretation, not debug-only.</summary>
    public override string ToString() => CanonicalContractToken;
}
