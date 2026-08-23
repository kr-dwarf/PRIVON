using Privon.App;

namespace Privon.App.Tests;

// Phase 0.2I -- deterministic, registry-free double for IWindowsAutoStartRegistration. Backs a
// REAL WindowsAutoStartCoordinator in tests (that type is a plain concrete policy class, not
// itself faked -- matches this codebase's established "fake the seam, use the real policy type"
// precedent, e.g. a real ClipboardDecisionSessionPublisher backed by a real
// ClipboardDecisionScopeLifecycle elsewhere). No mocking framework -- hand-written, matching every
// other Fake* in this project.
internal sealed class FakeWindowsAutoStartRegistration : IWindowsAutoStartRegistration
{
    private readonly Dictionary<string, string> _values = [];

    public int TryGetValueCallCount { get; private set; }
    public int TrySetValueCallCount { get; private set; }
    public int TryDeleteValueCallCount { get; private set; }

    public List<(string ValueName, string CommandLine)> SetCalls { get; } = [];
    public List<string> DeleteCalls { get; } = [];

    /// <summary>When set, <see cref="TrySetValue"/> always fails (returns false, never actually
    /// stores anything) -- lets a test force a deterministic ENABLE failure.</summary>
    public bool FailSet { get; set; }

    /// <summary>When set, <see cref="TryDeleteValue"/> always fails (returns false, never actually
    /// removes anything) -- lets a test force a deterministic DISABLE failure.</summary>
    public bool FailDelete { get; set; }

    /// <summary>Test-only seeding hook -- lets a test set up an "already registered" (possibly
    /// stale/different-path) starting state without going through <see cref="TrySetValue"/> itself.
    /// </summary>
    public void Seed(string valueName, string commandLine) => _values[valueName] = commandLine;

    public bool TryGetValue(string valueName, out string? commandLine)
    {
        TryGetValueCallCount++;
        return _values.TryGetValue(valueName, out commandLine);
    }

    public bool TrySetValue(string valueName, string commandLine)
    {
        TrySetValueCallCount++;
        SetCalls.Add((valueName, commandLine));
        if (FailSet) return false;

        _values[valueName] = commandLine;
        return true;
    }

    public bool TryDeleteValue(string valueName)
    {
        TryDeleteValueCallCount++;
        DeleteCalls.Add(valueName);
        if (FailDelete) return false;

        _values.Remove(valueName);
        return true;
    }
}
