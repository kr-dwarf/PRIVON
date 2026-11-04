namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §22.5 -- the one renderer D3B0 ships. NO LOCALIZATION IS IMPLEMENTED: it returns
/// the canonical contract token verbatim, so display and identity are byte-identical today and
/// a future localized renderer changes only display.
/// </summary>
internal sealed class ContractDefaultAliasTokenRenderer : IAliasTokenRenderer
{
    public string Render(AliasIdentity identity, ReplacementLanguage language)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (language != ReplacementLanguage.ContractDefault)
            throw new ArgumentOutOfRangeException(nameof(language), language, "Unsupported replacement language.");
        return identity.CanonicalContractToken;
    }
}
