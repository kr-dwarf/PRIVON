using System.Runtime.CompilerServices;

// Phase 3A.4 STEP2 test seam: exposes the internal IClipboardMonitorNative abstraction and its
// fake implementations to the Windows integration test project only. No public production API
// is widened for testing purposes -- see IClipboardMonitorNative.cs.
[assembly: InternalsVisibleTo("Privon.Windows.IntegrationTests")]
