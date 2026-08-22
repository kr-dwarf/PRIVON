namespace Privon.Windows;

/// <summary>
/// Phase 3C STEP29 -- the result of one <see cref="ComposerTextReader.ReadFocusedComposerTextAsync"/>
/// call. <see cref="Text"/> is populated only when <see cref="Outcome"/> is
/// <see cref="ComposerReadOutcome.Success"/> -- callers must check <see cref="Outcome"/> first,
/// never assume <see cref="Text"/> is present. Mirrors <see cref="ClipboardTextReadResult"/>'s own
/// minimal shape exactly (<c>Outcome</c> + one nullable payload) -- deliberately no
/// <c>Win32Error</c>-shaped field: a UI Automation failure is a COM/HRESULT-family condition, not
/// a Win32 GetLastError() one, so reusing that field's existing meaning here would be misleading;
/// a dedicated diagnostic field can be added later if a real need for it is ever demonstrated.
///
/// <see cref="Text"/> necessarily carries raw, current, potentially-sensitive composer content on
/// success -- it exists ONLY as this method's return value. Nothing in this type, or anywhere in
/// this assembly, stores it in a field, logs it, or includes it in an exception message.
///
/// RAW_COMPOSER_DIAGNOSTIC_SURFACE (mirrors <see cref="ClipboardTextSnapshot"/>'s own
/// RAW_CLIPBOARD_DIAGNOSTIC_SURFACE precedent, Phase 3A.4 STEP3.2): <see cref="ToString"/> is
/// explicitly overridden to expose <see cref="Outcome"/> only -- not even a length. The safest
/// contract is no content-derived metadata at all (Phase 3C STEP28's own PLACEHOLDER_EMPTY_POLICY/
/// DIAGNOSTIC_SURFACE finding explicitly preferred omitting length over including it).
/// </summary>
public readonly record struct ComposerTextReadResult
{
    public required ComposerReadOutcome Outcome { get; init; }
    public string? Text { get; init; }

    public static ComposerTextReadResult Success(string text) =>
        new() { Outcome = ComposerReadOutcome.Success, Text = text };

    public static ComposerTextReadResult Failure(ComposerReadOutcome outcome)
    {
        if (outcome == ComposerReadOutcome.Success)
            throw new ArgumentException("Use Success(...) to construct a successful result.", nameof(outcome));
        return new ComposerTextReadResult { Outcome = outcome };
    }

    public override string ToString() => $"{nameof(ComposerTextReadResult)} {{ {nameof(Outcome)} = {Outcome} }}";
}
