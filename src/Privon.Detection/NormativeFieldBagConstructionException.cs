namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §7.3 -- the exact public construction failure of
/// <see cref="NormativeFieldBag.Create"/>.
///
/// BAG_CONSTRUCTION_IS_OUTSIDE_THE_RESOLVER_UNION: raised before any resolve call exists. It is
/// NOT a NORMATIVE_RESOLVER_OUTCOME variant and does not change the accepted 3 FailureKind /
/// 5 resolver variants / 4 resolver exception types (§15).
///
/// <see cref="Message"/> is overridden so this type owns its full observable message. The
/// inherited <see cref="ArgumentException"/> getter would append a framework-generated,
/// resource-loaded parameter suffix -- the same defect A.4 repaired for the grouper (§21.3.4a).
/// </summary>
public sealed class NormativeFieldBagConstructionException : ArgumentException
{
    public const string InvalidFieldName = "NORMATIVE_FIELD_BAG_INVALID_FIELD_NAME";
    public const string DuplicateFieldName = "NORMATIVE_FIELD_BAG_DUPLICATE_FIELD_NAME";
    public const string RecognizedNameCollision = "NORMATIVE_FIELD_BAG_RECOGNIZED_NAME_COLLISION";

    private readonly string _message;

    internal NormativeFieldBagConstructionException(string errorCode, string? offendingName)
        : base(BuildMessage(errorCode, offendingName), "fields")
    {
        ErrorCode = errorCode;
        OffendingName = offendingName;
        _message = BuildMessage(errorCode, offendingName);
    }

    /// <summary>One of the three frozen literals above.</summary>
    public string ErrorCode { get; }

    /// <summary>
    /// The canonically selected offending key; <c>null</c> when the selected key was itself null.
    /// PRIVACY: a caller-supplied SCHEMA IDENTIFIER, never derived from scanned text, so naming
    /// it leaks nothing. This is deliberately different from the R0.5 payload, which is a scan
    /// diagnostic and therefore stays constant (§11.2).
    /// </summary>
    public string? OffendingName { get; }

    /// <summary>Exactly <c>"&lt;ErrorCode&gt;: name=&lt;OffendingName ?? "&lt;null&gt;"&gt;"</c>.</summary>
    public override string Message => _message;

    // Invariant by construction: only frozen ASCII literals and the caller's own key.
    private static string BuildMessage(string errorCode, string? offendingName) =>
        errorCode + ": name=" + (offendingName ?? "<null>");
}
