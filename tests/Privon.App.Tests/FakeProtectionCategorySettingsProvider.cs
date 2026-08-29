using Privon.App;
using Privon.Storage;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IProtectionCategorySettingsProvider -- mirrors
// FakeTrustExceptionProvider exactly (same LoadCount tracking, same "no mocking framework"
// discipline). Real PrivonLocalStore-backed loading has its own dedicated coverage in
// ProtectionCategorySettingsProviderTests.cs.
internal sealed class FakeProtectionCategorySettingsProvider : IProtectionCategorySettingsProvider
{
    private readonly ProtectionCategorySettings _settingsToReturn;

    public int LoadCount { get; private set; }

    public FakeProtectionCategorySettingsProvider() : this(ProtectionCategorySettings.AllOn)
    {
    }

    public FakeProtectionCategorySettingsProvider(ProtectionCategorySettings settingsToReturn)
    {
        _settingsToReturn = settingsToReturn;
    }

    public ProtectionCategorySettings Load()
    {
        LoadCount++;
        return _settingsToReturn;
    }
}
