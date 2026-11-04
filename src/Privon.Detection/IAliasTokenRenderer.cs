namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §22.5 -- PRESENTATION ONLY.
///
/// RENDERER_NEVER_AFFECTS_IDENTITY: renderer output MUST NOT participate in
/// CandidateSemanticIdentity equality, R1 de-duplication, resolver arbitration, the CCSK,
/// hashing, detector identity, or occurrence numbering.
/// </summary>
public interface IAliasTokenRenderer
{
    string Render(AliasIdentity identity, ReplacementLanguage language);
}
