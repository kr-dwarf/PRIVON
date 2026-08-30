namespace Privon.Windows;

/// <summary>
/// Phase 3A.5 STEP2 -- Foreground Target Mechanical Primitive. Captures a single, fresh
/// <see cref="ForegroundTargetSnapshot"/> fact about the current foreground window/process, on
/// demand, with zero caching between calls and zero clipboard content APIs called anywhere in
/// this type or its dependencies.
///
/// This type knows nothing about product/privacy policy -- no "ChatGPT," no "supported target,"
/// no "eligible," no notion of PII/Protect/Alias/Risk. A future <c>Privon.App</c>-owned TargetGate
/// is the only place <see cref="ForegroundTargetSnapshot.ProcessName"/> is ever compared against a
/// configured target name (CHATGPT PROCESS NAME POLICY: that comparison, and its
/// <c>StringComparison.OrdinalIgnoreCase</c> choice, live entirely outside this type).
///
/// FOREGROUND_EXECUTION_GUARD (Phase 3A.5 STEP1 correction, recorded but NOT implemented by this
/// type): a single <see cref="Capture"/> call from an App-thread policy layer, by itself, cannot
/// eliminate the cross-thread-marshaling TOCTOU gap between "target checked eligible" and "raw
/// clipboard read/write actually executes" -- a future guarded read/write still needs its OWN
/// fresh foreground-PID-equality check performed on the clipboard owner thread, immediately before
/// OpenClipboard/EmptyClipboard, comparing against an explicit expected PID the App layer already
/// decided was eligible. This type is a building block for that future guard, not the guard
/// itself; it exists purely to answer "what is the foreground process right now," synchronously,
/// with no side effects.
/// </summary>
public sealed class ForegroundTargetInspector : IDisposable
{
    private readonly IForegroundTargetSource _source;

    public ForegroundTargetInspector() : this(new Win32ForegroundTargetSource())
    {
    }

    internal ForegroundTargetInspector(IForegroundTargetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>
    /// BUG-004 Gate 2H.3 (E+): a one-line delegation to <see cref="ForegroundIdentityCapture.TryCapture"/>,
    /// the single coherent-capture primitive this assembly's guarded transports also use -- so
    /// authorization-time capture and every execution-time guard can never diverge. See that type
    /// for the full CAPTURE_FLOW / FACT_CAPTURE_LINEARIZATION_POINT contract. Any ordinary failure
    /// yields an unresolved snapshot; never a stale prior snapshot, never a guess. Never throws for
    /// an ordinary condition; never calls any clipboard content API.
    /// </summary>
    public ForegroundTargetSnapshot Capture() =>
        ForegroundIdentityCapture.TryCapture(_source, out var snapshot) ? snapshot : default;

    /// <summary>
    /// PRIVON 0.3.0 Gate 1C.1 -- disposes the underlying <see cref="IForegroundTargetSource"/> if it
    /// holds disposable native resources (the real production <c>Win32ForegroundTargetSource</c>
    /// retains a process handle and an executable file handle as of Gate 1B/1C.1's process-bound
    /// signer-evidence reuse). Idempotency is that implementation's own responsibility -- this
    /// method adds no additional guard, matching this codebase's existing minimal-disposal
    /// discipline (e.g. <see cref="ClipboardChangeMonitor.Dispose"/>).
    /// </summary>
    public void Dispose()
    {
        (_source as IDisposable)?.Dispose();
    }
}
