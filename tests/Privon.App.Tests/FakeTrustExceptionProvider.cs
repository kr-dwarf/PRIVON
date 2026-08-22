using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for ITrustExceptionProvider -- lets ClipboardPrivacyProcessor's
// trust/exception evaluation orchestration be exercised (load timing, load count, unexpected
// failure propagation) without a real PrivonLocalStore. Real PrivonLocalStore-backed mapping
// behavior already has its own dedicated coverage in TrustExceptionProviderTests.cs (Phase 3B
// STEP6) -- this fake exists purely for the App-owned orchestration boundary, matching the
// FakeClipboardReadTransport/FakeForegroundTargetCapture/FakeClipboardPrivacyProcessor precedent
// already established in this project. No mocking framework.
internal sealed class FakeTrustExceptionProvider : ITrustExceptionProvider
{
    private readonly TrustExceptionSnapshot? _snapshotToReturn;
    private readonly Exception? _exceptionToThrow;

    public int LoadCount { get; private set; }

    public FakeTrustExceptionProvider() : this(new TrustExceptionSnapshot(trustedPublic: [], exceptions: []))
    {
    }

    public FakeTrustExceptionProvider(TrustExceptionSnapshot snapshotToReturn)
    {
        _snapshotToReturn = snapshotToReturn;
    }

    /// <summary>Makes <see cref="Load"/> throw instead of returning -- simulates an unexpected
    /// provider/programming failure (never a "known Storage corruption", which
    /// TrustExceptionProvider already resolves internally to a safe-empty snapshot).</summary>
    public FakeTrustExceptionProvider(Exception exceptionToThrow)
    {
        _exceptionToThrow = exceptionToThrow;
    }

    public TrustExceptionSnapshot Load()
    {
        LoadCount++;
        if (_exceptionToThrow is { } ex) throw ex;
        return _snapshotToReturn!;
    }
}
