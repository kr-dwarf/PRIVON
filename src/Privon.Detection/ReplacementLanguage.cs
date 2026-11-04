namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §22.5 -- closed at one member; extended only by an audited amendment. Adding a
/// member changes ONLY display and requires no change to AliasFamilyId, Occurrence,
/// CanonicalContractToken, CandidateSemanticIdentity, or any resolver behaviour.
/// </summary>
public enum ReplacementLanguage
{
    ContractDefault = 0,
}
