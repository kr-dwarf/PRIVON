namespace Privon.Detection;

/// <summary>
/// Phase 2T.1 -- an assigned alias for one typed canonical value, e.g. PiiType.Phone, 1,
/// "[전화번호1]". Deliberately minimal: no provenance, no UUID/random id, and no copy of the
/// original canonical PII value -- <see cref="Value"/> is only ever the display token text
/// itself, never the thing it stands in for.
/// </summary>
public readonly record struct AliasToken(PiiType PiiType, int Number, string Value);
