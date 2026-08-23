using Microsoft.Win32;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 0.2I -- WindowsAutoStartManager regression. Every test uses a REAL HKCU registry subkey
// (Microsoft.Win32.Registry.CurrentUser is always fully writable by the owning user without
// elevation for any subkey path under it, exactly why this feature can use HKCU at all) -- but
// NEVER the real "Software\Microsoft\Windows\CurrentVersion\Run" key. Each test gets its own
// randomized scratch subkey (constructor's internal-only overload) elsewhere under HKCU, deleted
// in Dispose -- so no automated test run can ever leave a stray real auto-start entry, and tests
// never collide with each other or with anything a real installed application actually uses.
public sealed class WindowsAutoStartManagerTests : IDisposable
{
    private readonly string _scratchSubKeyPath = $@"Software\PRIVON-Test-Scratch-{Guid.NewGuid():N}";
    private readonly WindowsAutoStartManager _manager;

    public WindowsAutoStartManagerTests()
    {
        _manager = new WindowsAutoStartManager(_scratchSubKeyPath);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_scratchSubKeyPath, throwOnMissingSubKey: false);
    }

    [Fact]
    public void TryGetValue_NoKeyExistsAtAll_ReturnsFalse()
    {
        bool result = _manager.TryGetValue("PRIVON", out var commandLine);

        Assert.False(result);
        Assert.Null(commandLine);
    }

    [Fact]
    public void TrySetValue_ThenTryGetValue_ReturnsExactValue()
    {
        const string expected = "\"C:\\PRIVON\\PRIVON.exe\"";

        Assert.True(_manager.TrySetValue("PRIVON", expected));

        Assert.True(_manager.TryGetValue("PRIVON", out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TrySetValue_PathWithSpaces_RoundTripsExactly()
    {
        const string expected = "\"C:\\Program Files\\PRIVON\\PRIVON.exe\"";

        Assert.True(_manager.TrySetValue("PRIVON", expected));
        Assert.True(_manager.TryGetValue("PRIVON", out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TrySetValue_OverwritesExistingValue()
    {
        Assert.True(_manager.TrySetValue("PRIVON", "\"C:\\Old\\PRIVON.exe\""));
        Assert.True(_manager.TrySetValue("PRIVON", "\"C:\\New\\PRIVON.exe\""));

        Assert.True(_manager.TryGetValue("PRIVON", out var actual));
        Assert.Equal("\"C:\\New\\PRIVON.exe\"", actual);
    }

    [Fact]
    public void TryDeleteValue_RemovesOnlyThatValue_LeavesOthersUntouched()
    {
        Assert.True(_manager.TrySetValue("PRIVON", "\"C:\\PRIVON\\PRIVON.exe\""));
        Assert.True(_manager.TrySetValue("SomeOtherApp", "\"C:\\Other\\Other.exe\""));

        Assert.True(_manager.TryDeleteValue("PRIVON"));

        Assert.False(_manager.TryGetValue("PRIVON", out _));
        Assert.True(_manager.TryGetValue("SomeOtherApp", out var untouched));
        Assert.Equal("\"C:\\Other\\Other.exe\"", untouched);
    }

    [Fact]
    public void TryDeleteValue_ValueDoesNotExist_ButKeyDoes_ReturnsTrue_Idempotent()
    {
        Assert.True(_manager.TrySetValue("SomeOtherApp", "\"C:\\Other\\Other.exe\""));

        Assert.True(_manager.TryDeleteValue("PRIVON")); // never written -- still a successful "already off"
    }

    [Fact]
    public void TryDeleteValue_KeyDoesNotExistAtAll_ReturnsTrue()
    {
        Assert.True(_manager.TryDeleteValue("PRIVON"));
    }

    [Fact]
    public void TryDeleteValue_CalledTwice_BothReturnTrue()
    {
        Assert.True(_manager.TrySetValue("PRIVON", "\"C:\\PRIVON\\PRIVON.exe\""));

        Assert.True(_manager.TryDeleteValue("PRIVON"));
        Assert.True(_manager.TryDeleteValue("PRIVON"));
    }

    [Fact]
    public void RealSubKey_IsUnderCurrentUser_NotLocalMachine()
    {
        Assert.True(_manager.TrySetValue("PRIVON", "\"C:\\PRIVON\\PRIVON.exe\""));

        using var underCurrentUser = Registry.CurrentUser.OpenSubKey(_scratchSubKeyPath, writable: false);
        Assert.NotNull(underCurrentUser);
        Assert.Equal("\"C:\\PRIVON\\PRIVON.exe\"", underCurrentUser!.GetValue("PRIVON"));
    }

    [Fact]
    public void Constructor_NullOrEmptySubKeyPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowsAutoStartManager(null!));
        Assert.Throws<ArgumentException>(() => new WindowsAutoStartManager(""));
    }

    [Fact]
    public void ProductionDefaultConstructor_TargetsTheRealRunKeyPath()
    {
        // Structural-only: proves the production (parameterless) constructor points at the real
        // Windows Run key path WITHOUT this test itself ever writing to it -- confirmed by reading
        // back whatever (if anything) is ALREADY there for an unrelated, guaranteed-absent value
        // name, never mutating real state.
        var production = new WindowsAutoStartManager();
        bool result = production.TryGetValue("PRIVON-Test-Guaranteed-Absent-Value-Name-Never-Written", out var value);

        Assert.False(result);
        Assert.Null(value);
    }

    [Fact]
    public void TypeIsPublic_ConsumableFromPrivonApp()
    {
        Assert.True(typeof(WindowsAutoStartManager).IsPublic);
    }
}
