using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3C STEP41.1 -- Native Clipboard Notification + Self-Write Suppression Diagnostic
// Coverage regression. All tests use FakeClipboardMonitorNative (synthetic, OS-free). No real
// Windows clipboard/foreground state is ever touched. Mirrors
// ClipboardChangeMonitorWriteTests.cs's own SELF_WRITE_SUPPRESSION_MARKER_LIFECYCLE test
// precedent (a follow-up ReadTextSnapshotAsync call as a deterministic FIFO synchronization
// barrier -- no timing-dependent waits).
public class ClipboardChangeMonitorDiagnosticTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private const uint DefaultSequence = 5;

    private static (ClipboardChangeMonitor Monitor, FakeClipboardMonitorNative Native) CreateStarted()
    {
        var native = new FakeClipboardMonitorNative { UnicodeTextAvailable = true, SequenceNumber = DefaultSequence };
        var textNative = new FakeClipboardTextNative();
        // BUG-006: default original-content baseline -- see ClipboardChangeMonitorWriteTests's own
        // CreateStarted for the full rationale (needed for this file's Success-path writes' new
        // pre-EmptyClipboard rollback-backup read to succeed).
        textNative.SetUnicodeTextPayload("ORIGINAL-CLIPBOARD-TEXT");
        var monitor = new ClipboardChangeMonitor(native, textNative: textNative);
        monitor.Start();
        return (monitor, native);
    }

    // ==================================================================
    // A. NATIVE -> SUPPRESSION -> Changed BOUNDARY VISIBILITY
    // ==================================================================

    // ---- 1. an ordinary external change: NativeNotificationReceived then ExternalChangeRaised,
    // same LocalEventId, exact SequenceNumber match with the Changed notification that follows ----
    [Fact]
    public async Task ExternalChange_RecordsReceivedThenExternalChangeRaised_SameLocalEventId_ExactSequenceMatch()
    {
        var (monitor, native) = CreateStarted();
        var diagnostics = new List<ClipboardMonitorDiagnosticEvent>();
        var received = new List<ClipboardChangeNotification>();
        monitor.DiagnosticObserved += (_, e) => diagnostics.Add(e);
        monitor.Changed += (_, n) => received.Add(n);

        native.SequenceNumber = 42;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout); // FIFO sync barrier
        monitor.Stop();

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(ClipboardMonitorDiagnosticKind.NativeNotificationReceived, diagnostics[0].Kind);
        Assert.Equal(ClipboardMonitorDiagnosticKind.ExternalChangeRaised, diagnostics[1].Kind);
        Assert.Equal(diagnostics[0].LocalEventId, diagnostics[1].LocalEventId);
        Assert.Equal(42u, diagnostics[0].SequenceNumber);
        Assert.Equal(42u, diagnostics[1].SequenceNumber);
        Assert.True(diagnostics[1].HasReliableSequence);

        Assert.Single(received);
        // CROSS_LAYER_CORRELATION: exact same SequenceNumber as the App-visible notification.
        Assert.Equal(diagnostics[1].SequenceNumber, received[0].SequenceNumber);
    }

    // ---- 2. self-write suppression: NativeNotificationReceived then SelfWriteSuppressed, same
    // LocalEventId, Changed NOT raised -- no App attempt is ever expected to follow ----
    [Fact]
    public async Task SelfWriteSuppressed_RecordsReceivedThenSuppressed_SameLocalEventId_ChangedNeverRaised()
    {
        var (monitor, native) = CreateStarted();
        var diagnostics = new List<ClipboardMonitorDiagnosticEvent>();
        var received = new List<ClipboardChangeNotification>();
        monitor.DiagnosticObserved += (_, e) => diagnostics.Add(e);
        monitor.Changed += (_, n) => received.Add(n);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        Assert.Equal(ClipboardWriteOutcome.Success, result.Outcome);
        uint writeSequence = result.ResultSequence!.Value;
        diagnostics.Clear(); // isolate diagnostics for the self-write echo notification only

        native.SequenceNumber = writeSequence;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout); // FIFO sync barrier
        monitor.Stop();

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(ClipboardMonitorDiagnosticKind.NativeNotificationReceived, diagnostics[0].Kind);
        Assert.Equal(ClipboardMonitorDiagnosticKind.SelfWriteSuppressed, diagnostics[1].Kind);
        Assert.Equal(diagnostics[0].LocalEventId, diagnostics[1].LocalEventId);
        Assert.Equal(writeSequence, diagnostics[1].SequenceNumber);
        Assert.DoesNotContain(diagnostics, e => e.Kind == ClipboardMonitorDiagnosticKind.ExternalChangeRaised);

        Assert.Empty(received); // Changed was never invoked for the suppressed notification.
    }

    // ---- 3. repeated self-write echoes (multiple queued WM_CLIPBOARDUPDATE for one write) each
    // produce their own Received+Suppressed pair with a DIFFERENT LocalEventId -- proving the
    // counter is per-native-event, not per-write ----
    [Fact]
    public async Task RepeatedSelfWriteEchoes_EachProducesOwnLocalEventId_AllSuppressed()
    {
        var (monitor, native) = CreateStarted();
        var diagnostics = new List<ClipboardMonitorDiagnosticEvent>();
        monitor.DiagnosticObserved += (_, e) => diagnostics.Add(e);

        var result = await monitor.WriteTextIfSequenceMatchesAsync(DefaultSequence, "hello").WaitAsync(WaitTimeout);
        uint writeSequence = result.ResultSequence!.Value;
        diagnostics.Clear();
        native.SequenceNumber = writeSequence;

        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        var suppressedIds = diagnostics
            .Where(e => e.Kind == ClipboardMonitorDiagnosticKind.SelfWriteSuppressed)
            .Select(e => e.LocalEventId)
            .ToList();
        Assert.Equal(2, suppressedIds.Count);
        Assert.NotEqual(suppressedIds[0], suppressedIds[1]); // distinct native events, distinct ids
    }

    // ---- 4. unreliable (zero) sequence: still delivered as ExternalChangeRaised, never
    // classified as a self-write (0 is never trusted as a suppression-comparison baseline) ----
    [Fact]
    public async Task UnreliableSequenceZero_StillRaisesExternalChange_NeverSuppressed()
    {
        var (monitor, native) = CreateStarted();
        var diagnostics = new List<ClipboardMonitorDiagnosticEvent>();
        var received = new List<ClipboardChangeNotification>();
        monitor.DiagnosticObserved += (_, e) => diagnostics.Add(e);
        monitor.Changed += (_, n) => received.Add(n);

        native.SequenceNumber = 0;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        var externalEvent = Assert.Single(diagnostics, e => e.Kind == ClipboardMonitorDiagnosticKind.ExternalChangeRaised);
        Assert.Equal(0u, externalEvent.SequenceNumber);
        Assert.False(externalEvent.HasReliableSequence);
        Assert.DoesNotContain(diagnostics, e => e.Kind == ClipboardMonitorDiagnosticKind.SelfWriteSuppressed);
        Assert.Single(received);
    }

    // ---- 5. LocalEventId is monotonic and never reused across separate native notifications ----
    [Fact]
    public async Task LocalEventId_IsMonotonicallyIncreasing_AcrossSeparateNativeNotifications()
    {
        var (monitor, native) = CreateStarted();
        var diagnostics = new List<ClipboardMonitorDiagnosticEvent>();
        monitor.DiagnosticObserved += (_, e) => diagnostics.Add(e);

        native.SequenceNumber = 10;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        native.SequenceNumber = 11;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        var receivedIds = diagnostics
            .Where(e => e.Kind == ClipboardMonitorDiagnosticKind.NativeNotificationReceived)
            .Select(e => e.LocalEventId)
            .ToList();
        Assert.Equal(2, receivedIds.Count);
        Assert.True(receivedIds[1] > receivedIds[0]);
    }

    // ---- 6. diagnostic observer subscriber-exception-isolation: mirrors Changed's own precedent
    // exactly -- a throwing DiagnosticObserved handler never affects Changed delivery ----
    [Fact]
    public async Task ThrowingDiagnosticObserver_NeverPreventsChangedDelivery()
    {
        var (monitor, native) = CreateStarted();
        var received = new List<ClipboardChangeNotification>();
        monitor.DiagnosticObserved += (_, _) => throw new InvalidOperationException("Synthetic diagnostic observer failure.");
        monitor.Changed += (_, n) => received.Add(n);

        native.SequenceNumber = 99;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Single(received);
        Assert.Equal(99u, received[0].SequenceNumber);
    }

    // ---- 7. zero subscribers (the production default) -- Changed delivery is bit-for-bit
    // identical to every existing pre-STEP41.1 test in this project (no new behavior) ----
    [Fact]
    public async Task NoSubscribers_ChangedDeliveryUnaffected()
    {
        var (monitor, native) = CreateStarted();
        var received = new List<ClipboardChangeNotification>();
        monitor.Changed += (_, n) => received.Add(n);
        // Deliberately no DiagnosticObserved subscription at all.

        native.SequenceNumber = 7;
        native.RaiseClipboardUpdate();
        await monitor.ReadTextSnapshotAsync().WaitAsync(WaitTimeout);
        monitor.Stop();

        Assert.Single(received);
        Assert.Equal(7u, received[0].SequenceNumber);
    }

    // ==================================================================
    // B. PRIVACY BOUNDARY -- structural
    // ==================================================================

    [Fact]
    public void ClipboardMonitorDiagnosticEvent_HasNoStringMembers()
    {
        var stringMembers = typeof(ClipboardMonitorDiagnosticEvent)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m =>
                (m is PropertyInfo p && p.PropertyType == typeof(string)) ||
                (m is FieldInfo f && f.FieldType == typeof(string)));

        Assert.Empty(stringMembers);
    }

    [Fact]
    public void ClipboardMonitorDiagnosticEvent_HasNoDebuggerDisplayOrTypeProxyAttributes()
    {
        var attributes = typeof(ClipboardMonitorDiagnosticEvent).GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);
        Assert.DoesNotContain(attributes, name => name is "DebuggerDisplayAttribute" or "DebuggerTypeProxyAttribute");
    }

    [Fact]
    public void ClipboardMonitorDiagnosticEvent_OnlyExpectedPrimitiveFields()
    {
        var properties = typeof(ClipboardMonitorDiagnosticEvent).GetProperties();
        var names = properties.Select(p => p.Name).ToList();

        Assert.Equal(4, properties.Length);
        Assert.Contains(nameof(ClipboardMonitorDiagnosticEvent.LocalEventId), names);
        Assert.Contains(nameof(ClipboardMonitorDiagnosticEvent.Kind), names);
        Assert.Contains(nameof(ClipboardMonitorDiagnosticEvent.SequenceNumber), names);
        Assert.Contains(nameof(ClipboardMonitorDiagnosticEvent.HasReliableSequence), names);

        Assert.All(properties, p => Assert.True(
            p.PropertyType == typeof(long) || p.PropertyType == typeof(uint) ||
            p.PropertyType == typeof(bool) || p.PropertyType == typeof(ClipboardMonitorDiagnosticKind)));
    }

    // ---- no ChatGPT/product-policy naming anywhere in the new diagnostic types either (mirrors
    // ClipboardChangeMonitor_HasNoProductPolicyNaming's existing precedent) ----
    [Fact]
    public void ClipboardMonitorDiagnosticTypes_HaveNoProductPolicyNaming()
    {
        var forbidden = new[] { "ChatGPT", "IsChatGPT", "IsSupportedAi", "IsEligible", "PrivacyGate" };

        foreach (var type in new[] { typeof(ClipboardMonitorDiagnosticEvent), typeof(ClipboardMonitorDiagnosticKind) })
        {
            var names = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name);
            Assert.DoesNotContain(names, name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
