namespace Privon.Storage;

/// <summary>
/// Versioned on-disk envelope: [1 byte formatVersion][12 byte nonce][16 byte tag][ciphertext].
/// Contains no plaintext and no key material.
/// </summary>
public sealed class SecureEnvelope
{
    public const byte CurrentFormatVersion = 1;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public byte FormatVersion { get; }
    public byte[] Nonce { get; }
    public byte[] Tag { get; }
    public byte[] Ciphertext { get; }

    public SecureEnvelope(byte formatVersion, byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        FormatVersion = formatVersion;
        Nonce = nonce;
        Tag = tag;
        Ciphertext = ciphertext;
    }

    public byte[] Encode()
    {
        var result = new byte[1 + NonceSize + TagSize + Ciphertext.Length];
        result[0] = FormatVersion;
        Nonce.CopyTo(result, 1);
        Tag.CopyTo(result, 1 + NonceSize);
        Ciphertext.CopyTo(result, 1 + NonceSize + TagSize);
        return result;
    }

    public static SecureEnvelope Decode(byte[] bytes)
    {
        const int headerSize = 1 + NonceSize + TagSize;
        if (bytes.Length < headerSize)
        {
            throw new EnvelopeFormatException($"Envelope too short: {bytes.Length} bytes.");
        }

        var formatVersion = bytes[0];
        if (formatVersion != CurrentFormatVersion)
        {
            throw new EnvelopeFormatException($"Unknown envelope formatVersion: {formatVersion}.");
        }

        var nonce = bytes.AsSpan(1, NonceSize).ToArray();
        var tag = bytes.AsSpan(1 + NonceSize, TagSize).ToArray();
        var ciphertext = bytes.AsSpan(headerSize).ToArray();
        return new SecureEnvelope(formatVersion, nonce, tag, ciphertext);
    }
}
