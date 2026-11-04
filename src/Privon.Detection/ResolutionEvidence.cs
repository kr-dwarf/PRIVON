namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §13 -- closed evidence domain. Positions 0-6 reproduce the D2.2 lattice
/// <c>S+C+J/P &gt; S+C &gt; S+J/P &gt; C+J/P &gt; S &gt; C &gt; J/P</c>. Position 7
/// (<see cref="NotModelled"/>) is SERIALIZATION-ONLY and confers no arbitration ordering --
/// safe only because of EVIDENCE_NEVER_READ_FOR_LEGACY (§9.2).
/// </summary>
public enum ResolutionEvidence
{
    JOrP = 0,
    C = 1,
    S = 2,
    CJP = 3,
    SJP = 4,
    SC = 5,
    SCJP = 6,
    NotModelled = 7,
}
