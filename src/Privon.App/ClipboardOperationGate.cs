namespace Privon.App;

/// <summary>
/// Phase 3B STEP23 -- the only production implementation of <see cref="IClipboardOperationGate"/>.
/// Owns exactly one <see cref="SemaphoreSlim"/> initialized to (1, 1) -- a single permit, no other
/// state of any kind (no PII, no raw text, no <c>CanonicalValue</c>/<c>RevisionStamp</c>/scope/
/// target/snapshot/plan reference -- see this type's own structural test for the exact
/// single-field guarantee).
///
/// OPERATION_GATE_INSTANCE_IDENTITY (Phase 3B STEP22 audit, frozen): exactly ONE concrete instance
/// of this type exists per App runtime, shared -- via the narrow
/// <see cref="IClipboardOperationGate"/> interface -- by BOTH <see cref="ClipboardPrivacyCoordinator"/>
/// and <see cref="ClipboardDecisionActionResolver"/> (see <see cref="PrivonAppComposition.BuildGraph"/>,
/// which constructs exactly one instance and passes it to both). Two independent instances would
/// serialize nothing relative to each other and would silently defeat the entire point of this type
/// -- see
/// <c>ClipboardOperationGateTests.SameGateInstance_SharedByTwoIndependentConsumers_SerializesBetweenThem</c>.
///
/// Never disposed by <see cref="ClipboardPrivacyCoordinator"/> -- like
/// <see cref="IClipboardReadTransport"/>/<see cref="IForegroundTargetCapture"/>, this is an
/// injected, not owned, dependency; its disposal is <see cref="PrivonAppComposition"/>'s own
/// responsibility (see that type's own SHUTDOWN_ORDER doc).
/// </summary>
internal sealed class ClipboardOperationGate : IClipboardOperationGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);

    public Task WaitAsync() => _semaphore.WaitAsync();

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
