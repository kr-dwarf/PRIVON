using System.Reflection;
using Privon.App;

namespace Privon.App.Tests;

// Phase 3B STEP23 -- ClipboardOperationGate regression. Proves the exact async mutual-exclusion
// contract the Phase 3B STEP22 audit froze (SERIALIZATION_PRIMITIVE): a single async permit,
// acquired via WaitAsync, released via Release, no synchronous escape hatch, no sensitive state
// of any kind. No fakes -- this is the real, only production implementation, matching the same
// "no fake needed for a lightweight, OS-independent, deterministic managed-state primitive"
// precedent already established for ClipboardDecisionScopeLifecycle. No arbitrary sleeps anywhere
// in this file -- every ordering proof relies on SemaphoreSlim's own guarantee that a WaitAsync()
// call that cannot immediately acquire returns a task that is NOT yet completed at the moment
// WaitAsync() itself returns (checked synchronously via Task.IsCompleted, never inferred from a
// timing window).
public class ClipboardOperationGateTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    // ---- U.1. first async acquisition succeeds ----
    [Fact]
    public async Task WaitAsync_FirstAcquisition_Succeeds()
    {
        var gate = new ClipboardOperationGate();

        await gate.WaitAsync().WaitAsync(WaitTimeout);

        gate.Release();
    }

    // ---- U.2/U.3. a second waiter does not complete before the first releases; release allows
    // the next waiter to proceed ----
    [Fact]
    public async Task WaitAsync_SecondWaiter_DoesNotCompleteUntilFirstReleases()
    {
        var gate = new ClipboardOperationGate();
        await gate.WaitAsync();

        var secondWaitTask = gate.WaitAsync();
        Assert.False(secondWaitTask.IsCompleted);

        gate.Release();
        await secondWaitTask.WaitAsync(WaitTimeout);

        gate.Release();
    }

    // ---- U.4. repeated acquire/release works ----
    [Fact]
    public async Task WaitAsync_RepeatedAcquireRelease_Works()
    {
        var gate = new ClipboardOperationGate();

        for (int i = 0; i < 5; i++)
        {
            await gate.WaitAsync().WaitAsync(WaitTimeout);
            gate.Release();
        }
    }

    // ---- U.5. no more than one holder at once -- three chained waiters, each released in turn;
    // the third must still be pending after only the first releases, and only completes once the
    // second (the sole legitimate next holder) has also released ----
    [Fact]
    public async Task WaitAsync_NeverAllowsMoreThanOneConcurrentHolder()
    {
        var gate = new ClipboardOperationGate();
        await gate.WaitAsync(); // holder 1

        var wait2 = gate.WaitAsync();
        var wait3 = gate.WaitAsync();
        Assert.False(wait2.IsCompleted);
        Assert.False(wait3.IsCompleted);

        gate.Release(); // holder 1 releases -> at most holder 2 may now proceed
        await wait2.WaitAsync(WaitTimeout);
        Assert.False(wait3.IsCompleted); // holder 3 must still be excluded

        gate.Release(); // holder 2 releases -> holder 3 may now proceed
        await wait3.WaitAsync(WaitTimeout);

        gate.Release();
    }

    // ---- U.6. no synchronous Wait/Result-style production API is exposed ----
    [Fact]
    public void IClipboardOperationGate_HasNoSynchronousAcquisitionMember()
    {
        var members = typeof(IClipboardOperationGate).GetMembers().Select(m => m.Name);

        Assert.DoesNotContain(members, name => name is "Wait" or "Result" or "GetAwaiter");
    }

    // ---- U.7. the gate contains no sensitive fields of any kind -- exactly one field, the
    // SemaphoreSlim itself, and nothing PII-adjacent ----
    [Fact]
    public void ClipboardOperationGate_HasOnlySemaphoreField()
    {
        var fields = typeof(ClipboardOperationGate)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
        Assert.DoesNotContain(fields, f => f.FieldType.Namespace == "Privon.Core");
        Assert.DoesNotContain(fields, f => f.FieldType.Namespace == "Privon.Detection");
        var field = Assert.Single(fields);
        Assert.Equal(typeof(SemaphoreSlim), field.FieldType);
    }

    // ---- U.8. no DebuggerDisplay/DebuggerTypeProxy leak ----
    [Fact]
    public void ClipboardOperationGate_HasNoDebuggerDisplayOrTypeProxyAttributes()
    {
        var attributes = typeof(ClipboardOperationGate).GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }

    // ==================================================================
    // SAME-INSTANCE FUTURE PROOF (section W)
    // ==================================================================

    // ---- proves the abstraction supports being shared, unmodified, by two independent
    // consumers -- one side holds it, the other cannot enter until release -- exactly the sharing
    // model a future ClipboardDecisionActionResolver holding the SAME concrete instance as the
    // coordinator would require (Phase 3B STEP22 audit's OPERATION_GATE_INSTANCE_IDENTITY). No
    // resolver is implemented here -- this only freezes that the gate ITSELF already supports
    // that sharing model. ----
    [Fact]
    public async Task SameGateInstance_SharedByTwoIndependentConsumers_SerializesBetweenThem()
    {
        var operationGate = new ClipboardOperationGate();
        IClipboardOperationGate coordinatorView = operationGate;
        IClipboardOperationGate hypotheticalSecondConsumerView = operationGate;

        await coordinatorView.WaitAsync();

        var secondConsumerWait = hypotheticalSecondConsumerView.WaitAsync();
        Assert.False(secondConsumerWait.IsCompleted);

        coordinatorView.Release();
        await secondConsumerWait.WaitAsync(WaitTimeout);

        hypotheticalSecondConsumerView.Release();
    }

    // ==================================================================
    // ACCESSIBILITY
    // ==================================================================

    [Theory]
    [InlineData(typeof(IClipboardOperationGate))]
    [InlineData(typeof(ClipboardOperationGate))]
    public void GateTypes_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }
}
