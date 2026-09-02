namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031F1R -- the frozen, mechanical, product-policy-free decode-status set for
/// <see cref="WebFrameDecoder.Decode"/>. Exactly these ten values, no more (Gate 031F1R's own
/// instruction: "Do not add additional success/degraded statuses"). Carries no opinion about
/// which origin/browser is supported -- this type answers only "did this byte buffer decode into
/// a well-formed protocol message", never "is it one PRIVON should authorize".
///
/// SAFE_VALUE_FIRST (this codebase's established discipline -- see e.g.
/// <c>PackageIdentityResolution.Unresolved</c>, <c>ExecutableSignatureResolution.NotInspected</c>):
/// <see cref="Incomplete"/> is declared first (value 0), and <see cref="Ok"/> last, deliberately --
/// a caller that forgets to check this value, or receives a default-initialized
/// <see cref="WebFrameDecodeStatus"/>, can never mistake silence for a successfully decoded
/// message. The frozen contract fixes the NAME set and the validation order that produces each
/// value; it does not fix underlying ordinal values, so this ordering is a deliberate,
/// repository-consistent choice, not a contract violation.
/// </summary>
public enum WebFrameDecodeStatus
{
    /// <summary>Fewer than 4 bytes are available for the length prefix, or the declared payload
    /// length is not yet fully available in the supplied buffer. The ordinary, expected state for
    /// a buffer that has not yet accumulated a whole frame.</summary>
    Incomplete,

    /// <summary>The decoded 32-bit length prefix is exactly zero.</summary>
    InvalidLength,

    /// <summary>The decoded length prefix exceeds the maximum payload size
    /// (<see cref="WebFrameDecoder.MaxPayloadBytes"/>) -- detected BEFORE any payload buffer is
    /// allocated or copied. An ordinary small payload whose length happened to be encoded in the
    /// wrong (big-endian) byte order also lands here, by design.</summary>
    Oversized,

    /// <summary>The payload is not valid UTF-8 under strict decoding (no lossy replacement, no
    /// fallback encoding).</summary>
    InvalidUtf8,

    /// <summary>The UTF-8 text is not a single, complete, syntactically valid JSON value, or its
    /// top-level value is not a JSON object.</summary>
    MalformedJson,

    /// <summary>The <c>v</c> property is absent, is not a JSON integer, or is a JSON integer other
    /// than the one frozen supported value.</summary>
    UnsupportedVersion,

    /// <summary>The <c>type</c> property is absent, or its value does not exactly (case-sensitive,
    /// <see cref="StringComparison.Ordinal"/>) match one of the six frozen message-family
    /// names.</summary>
    UnknownType,

    /// <summary>A field the identified message type requires is absent from the JSON object.
    /// Never reported before <see cref="UnsupportedVersion"/>/<see cref="UnknownType"/> have
    /// already passed -- an unrecognized version or type is reported as that, never as a missing
    /// field.</summary>
    MissingField,

    /// <summary>The JSON object itself is malformed as a PROTOCOL shape (a duplicate property
    /// name, a property not in the identified message type's allowed set), or a present field's
    /// value fails its own value-shape rule (wrong JSON kind, wrong enum spelling/casing, the
    /// OriginResolution/Origin presence invariant, or a syntactically invalid nonce
    /// representation).</summary>
    InvalidValue,

    /// <summary>The buffer decoded into exactly one well-formed message of a known type, with
    /// exactly its required and allowed fields present and individually valid.</summary>
    Ok,
}
