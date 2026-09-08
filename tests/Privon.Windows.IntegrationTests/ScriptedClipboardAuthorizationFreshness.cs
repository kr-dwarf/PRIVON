using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.1 Gate 031F4 -- TEST-ONLY deterministic double for IClipboardAuthorizationFreshness.
// Not a production scripted verifier of any kind: it exists solely so R4's real behavioral tests
// can drive ClipboardChangeMonitor's CHECK1/CHECK2 freshness seam through a known sequence of
// true/false answers (or a thrown exception) without any real Web evidence, channel, or Native
// Messaging concept ever existing in this assembly.
//
// CallCount lets a test assert the exact number of IsStillCurrent() invocations (1 for a CHECK1
// failure, 2 for a CHECK2 failure) -- proving there is no hidden third check. Calling this fake
// more times than it was scripted for throws InvalidOperationException, which is itself a
// positive assertion: a test relying on this fake can never silently pass past an unexpected
// extra call.
internal sealed class ScriptedClipboardAuthorizationFreshness : IClipboardAuthorizationFreshness
{
    private readonly Queue<bool> _results;
    private readonly Exception? _throwAfterResults;

    public int CallCount { get; private set; }

    public ScriptedClipboardAuthorizationFreshness(params bool[] results)
        : this(results, throwAfterResults: null)
    {
    }

    private ScriptedClipboardAuthorizationFreshness(bool[] results, Exception? throwAfterResults)
    {
        _results = new Queue<bool>(results);
        _throwAfterResults = throwAfterResults;
    }

    // Throws on the very first call -- e.g. simulating an exception surfacing at CHECK1.
    public static ScriptedClipboardAuthorizationFreshness Throwing(Exception exception) =>
        new([], exception);

    // Returns each of resultsBeforeThrow in order, then throws on the call after the last one --
    // e.g. simulating CHECK1 returning true followed by CHECK2 throwing.
    public static ScriptedClipboardAuthorizationFreshness ThenThrowing(Exception exception, params bool[] resultsBeforeThrow) =>
        new(resultsBeforeThrow, exception);

    public bool IsStillCurrent()
    {
        CallCount++;

        if (_results.Count > 0)
            return _results.Dequeue();

        if (_throwAfterResults is not null)
            throw _throwAfterResults;

        throw new InvalidOperationException(
            $"ScriptedClipboardAuthorizationFreshness.IsStillCurrent() was called {CallCount} " +
            "time(s) -- more than this test scripted. This indicates a hidden extra freshness " +
            "check beyond the two frozen CHECK1/CHECK2 sites.");
    }
}
