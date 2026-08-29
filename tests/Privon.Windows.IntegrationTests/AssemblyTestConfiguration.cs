using Xunit;

// BUG-006: ClipboardChangeMonitor's process-wide rollback quarantine slot (a deliberate design
// choice -- see its own doc -- so a quarantined handle survives any one monitor instance's
// disposal) is genuinely shared, static state. Its resolution is opportunistically attempted by
// EVERY ClipboardChangeMonitor.Stop()/Dispose() call in this entire assembly, not just the tests
// that deliberately exercise it (Bug006RecoveryTests). xUnit's default behavior runs different
// test classes (collections) in parallel, which would let one test's deliberately-induced
// quarantine/reservation state bleed into a completely unrelated, concurrently-running test's own
// Stop()/Dispose() call. Disabling test parallelization for this assembly is the smallest, most
// direct way to make the test suite itself deterministic against that shared state, without
// touching production code or introducing any new test-only locking/coordination mechanism.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
