using System.Security.Cryptography;

namespace Privon.Storage;

/// <summary>
/// AES-GCM encrypt/decrypt for envelope payloads. A fresh random nonce is generated per
/// encryption. The purpose string is bound in as AAD so payloads cannot be cross-decrypted
/// between stores.
/// </summary>
public static class SecureEnvelopeCodec
{
    public static SecureEnvelope Encrypt(byte[] key, string purpose, byte[] plaintext)
    {
        var nonce = new byte[SecureEnvelope.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[SecureEnvelope.TagSize];

        using var aesGcm = new AesGcm(key, SecureEnvelope.TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, StorePurpose.ToAad(purpose));

        return new SecureEnvelope(SecureEnvelope.CurrentFormatVersion, nonce, tag, ciphertext);
    }

    /// <summary>
    /// Decrypts an envelope. Throws <see cref="CryptographicException"/> on authentication
    /// failure (tampered ciphertext/tag, or wrong purpose/AAD) and
    /// <see cref="EnvelopeFormatException"/> on a malformed envelope. Callers must treat
    /// both as "corrupted -- fall back to safe defaults", never as partial success.
    /// </summary>
    public static byte[] Decrypt(byte[] key, string purpose, SecureEnvelope envelope)
    {
        var plaintext = new byte[envelope.Ciphertext.Length];
        using var aesGcm = new AesGcm(key, SecureEnvelope.TagSize);
        aesGcm.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, StorePurpose.ToAad(purpose));
        return plaintext;
    }
}
