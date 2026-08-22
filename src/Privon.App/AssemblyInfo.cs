using System.Runtime.CompilerServices;

// Phase 3B STEP2 test seam: exposes the internal App orchestration types (ClipboardPrivacyCoordinator,
// TargetGate, IClipboardReadTransport, IForegroundTargetCapture, and their production wrappers) to
// the App test project only. No public production API is widened for testing purposes -- mirrors
// Privon.Windows's own identical AssemblyInfo.cs pattern.
[assembly: InternalsVisibleTo("Privon.App.Tests")]
