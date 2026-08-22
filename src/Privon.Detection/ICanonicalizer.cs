namespace Privon.Detection;

/// <summary>
/// Produces a CanonicalValue from a detector's raw matched text. One implementation per
/// PiiType (PhoneCanonicalizer, EmailCanonicalizer, etc. as later detectors are added).
/// </summary>
public interface ICanonicalizer
{
    PiiType PiiType { get; }
    CanonicalValue Canonicalize(string rawMatchedText);
}
