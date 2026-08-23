using Privon.App;

namespace Privon.App.Tests;

// Phase 0.2I -- WindowsAutoStartCoordinator (the auto-start POLICY layer) regression. All tests use
// FakeWindowsAutoStartRegistration (synthetic, real-registry-free) -- no real HKCU access happens
// in this project (that remains Windows.IntegrationTests's own responsibility, matching this
// codebase's established App.Tests/Windows.IntegrationTests split everywhere else).
public class WindowsAutoStartCoordinatorTests
{
    private const string ExePath = "C:\\PRIVON\\PRIVON.exe";
    private const string ExpectedCommandLine = "\"C:\\PRIVON\\PRIVON.exe\"";

    private static WindowsAutoStartCoordinator CreateCoordinator(
        FakeWindowsAutoStartRegistration registration, string? currentExecutablePath = ExePath) =>
        new(registration, () => currentExecutablePath);

    // ---- A. DEFAULT OFF ----
    [Fact]
    public void IsEnabled_FreshNoRegistrationState_ReturnsFalse()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        var coordinator = CreateCoordinator(registration);

        Assert.False(coordinator.IsEnabled());
    }

    [Fact]
    public void IsEnabled_NeverWritesOrDeletesOnItsOwn()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        var coordinator = CreateCoordinator(registration);

        coordinator.IsEnabled();
        coordinator.IsEnabled();
        coordinator.IsEnabled();

        Assert.Equal(0, registration.TrySetValueCallCount);
        Assert.Equal(0, registration.TryDeleteValueCallCount);
    }

    [Fact]
    public void Constructor_NeverWritesOrDeletesOnItsOwn()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        _ = CreateCoordinator(registration);

        Assert.Equal(0, registration.TrySetValueCallCount);
        Assert.Equal(0, registration.TryDeleteValueCallCount);
    }

    // ---- B. ENABLE ----
    [Fact]
    public void TryEnable_FreshState_CreatesCurrentUserRegistration_WithExactCurrentPath()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        var coordinator = CreateCoordinator(registration);

        bool result = coordinator.TryEnable();

        Assert.True(result);
        var call = Assert.Single(registration.SetCalls);
        Assert.Equal(WindowsAutoStartCoordinator.ValueName, call.ValueName);
        Assert.Equal(ExpectedCommandLine, call.CommandLine);
    }

    [Fact]
    public void TryEnable_ThenIsEnabled_ReturnsTrue()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        var coordinator = CreateCoordinator(registration);

        Assert.True(coordinator.TryEnable());
        Assert.True(coordinator.IsEnabled());
    }

    // ---- C. DISABLE ----
    [Fact]
    public void TryDisable_RemovesOnlyThePrivonValue()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        registration.Seed("PRIVON", ExpectedCommandLine);
        registration.Seed("SomeOtherApp", "\"C:\\Other\\Other.exe\"");
        var coordinator = CreateCoordinator(registration);

        bool result = coordinator.TryDisable();

        Assert.True(result);
        Assert.Equal(new[] { WindowsAutoStartCoordinator.ValueName }, registration.DeleteCalls);
        Assert.False(registration.TryGetValue("PRIVON", out _));
        Assert.True(registration.TryGetValue("SomeOtherApp", out var untouched));
        Assert.Equal("\"C:\\Other\\Other.exe\"", untouched);
    }

    [Fact]
    public void TryDisable_ThenIsEnabled_ReturnsFalse()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        registration.Seed("PRIVON", ExpectedCommandLine);
        var coordinator = CreateCoordinator(registration);

        Assert.True(coordinator.TryDisable());
        Assert.False(coordinator.IsEnabled());
    }

    // ---- D. FAILURE ----
    [Fact]
    public void TryEnable_RegistryWriteFails_ReturnsFalse_DoesNotThrow_IsEnabledStaysFalse()
    {
        var registration = new FakeWindowsAutoStartRegistration { FailSet = true };
        var coordinator = CreateCoordinator(registration);

        bool result = coordinator.TryEnable();

        Assert.False(result);
        Assert.False(coordinator.IsEnabled());
    }

    [Fact]
    public void TryDisable_RegistryDeleteFails_ReturnsFalse_DoesNotThrow_RegistrationStillPresent()
    {
        var registration = new FakeWindowsAutoStartRegistration { FailDelete = true };
        registration.Seed("PRIVON", ExpectedCommandLine);
        var coordinator = CreateCoordinator(registration);

        bool result = coordinator.TryDisable();

        Assert.False(result);
        Assert.True(coordinator.IsEnabled()); // still registered -- failure never falsely reported as OFF
    }

    [Fact]
    public void TryEnable_UnresolvableCurrentPath_ReturnsFalse_NeverCallsRegistration()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        var coordinator = CreateCoordinator(registration, currentExecutablePath: null);

        bool result = coordinator.TryEnable();

        Assert.False(result);
        Assert.Equal(0, registration.TrySetValueCallCount);
    }

    [Fact]
    public void IsEnabled_UnresolvableCurrentPath_ReturnsFalse_NeverClaimsOn()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        registration.Seed("PRIVON", ExpectedCommandLine); // even with a real-looking existing entry
        var coordinator = CreateCoordinator(registration, currentExecutablePath: null);

        Assert.False(coordinator.IsEnabled());
    }

    // ---- E. EXISTING VALID REGISTRATION ----
    [Fact]
    public void IsEnabled_ExistingRegistration_ExactCurrentPath_ReturnsTrue()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        registration.Seed("PRIVON", ExpectedCommandLine);
        var coordinator = CreateCoordinator(registration);

        Assert.True(coordinator.IsEnabled());
    }

    // ---- F. STALE/DIFFERENT PATH ----
    [Fact]
    public void IsEnabled_ExistingRegistration_DifferentPath_ReturnsFalse_NeverFalsePositiveOn()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        registration.Seed("PRIVON", "\"C:\\OldLocation\\PRIVON.exe\"");
        var coordinator = CreateCoordinator(registration);

        Assert.False(coordinator.IsEnabled());
    }

    [Fact]
    public void TryEnable_AfterStalePath_OverwritesWithCurrentPath_IsEnabledBecomesTrue()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        registration.Seed("PRIVON", "\"C:\\OldLocation\\PRIVON.exe\"");
        var coordinator = CreateCoordinator(registration);

        Assert.False(coordinator.IsEnabled());
        Assert.True(coordinator.TryEnable());
        Assert.True(coordinator.IsEnabled());

        Assert.True(registration.TryGetValue("PRIVON", out var final));
        Assert.Equal(ExpectedCommandLine, final);
    }

    // ---- G. QUOTING ----
    [Fact]
    public void TryEnable_PathContainsSpaces_StoresExactlyQuoted()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        var coordinator = CreateCoordinator(registration, currentExecutablePath: @"C:\Program Files\PRIVON\PRIVON.exe");

        Assert.True(coordinator.TryEnable());

        var call = Assert.Single(registration.SetCalls);
        Assert.Equal("\"C:\\Program Files\\PRIVON\\PRIVON.exe\"", call.CommandLine);
    }

    [Fact]
    public void IsEnabled_ExistingRegistration_ExactlyQuotedSpacedPath_ReturnsTrue()
    {
        var registration = new FakeWindowsAutoStartRegistration();
        registration.Seed("PRIVON", "\"C:\\Program Files\\PRIVON\\PRIVON.exe\"");
        var coordinator = CreateCoordinator(registration, currentExecutablePath: @"C:\Program Files\PRIVON\PRIVON.exe");

        Assert.True(coordinator.IsEnabled());
    }

    [Fact]
    public void IsEnabled_UnquotedStoredValue_EvenIfPathMatchesUnquoted_ReturnsFalse()
    {
        // An unquoted stored value (however it got there) never satisfies the coordinator's own
        // exact-quoted-command-line expectation -- no lenient/normalized comparison is ever
        // performed.
        var registration = new FakeWindowsAutoStartRegistration();
        registration.Seed("PRIVON", ExePath); // no quotes
        var coordinator = CreateCoordinator(registration);

        Assert.False(coordinator.IsEnabled());
    }

    // ---- Constructor / structural ----
    [Fact]
    public void Constructor_NullRegistration_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowsAutoStartCoordinator(null!));
    }

    [Fact]
    public void ValueName_IsLiterallyPrivon()
    {
        Assert.Equal("PRIVON", WindowsAutoStartCoordinator.ValueName);
    }
}
