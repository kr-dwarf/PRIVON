namespace Privon.Storage;

/// <summary>Thrown when a stored envelope's bytes are not a recognizable envelope (wrong
/// length, unknown format version). Treated identically to a decryption failure by callers
/// -- both fall back to safe defaults.</summary>
public sealed class EnvelopeFormatException : Exception
{
    public EnvelopeFormatException(string message) : base(message) { }
}
