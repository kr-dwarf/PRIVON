using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IUserExceptionProvider -- mirrors FakeTrustExceptionProvider/
// FakeProtectionCategorySettingsProvider exactly. Real PrivonLocalStore-backed loading has its own
// dedicated coverage in UserExceptionProviderTests.cs.
internal sealed class FakeUserExceptionProvider : IUserExceptionProvider
{
    private readonly IReadOnlyList<UserExceptionValue> _valuesToReturn;

    public int LoadCount { get; private set; }

    public FakeUserExceptionProvider() : this([])
    {
    }

    public FakeUserExceptionProvider(IReadOnlyList<UserExceptionValue> valuesToReturn)
    {
        _valuesToReturn = valuesToReturn;
    }

    public IReadOnlyList<UserExceptionValue> Load()
    {
        LoadCount++;
        return _valuesToReturn;
    }
}
