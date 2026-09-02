namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6F (E2) -- the structurally bounded shape of a resolved browser-host
/// process relationship. Deliberately only two members -- no recursion, no depth configuration, no
/// "Unknown" value (a topology that cannot be classified is a resolution FAILURE, carried by
/// <see cref="BrowserHostBindingStatus.UnknownIntermediary"/>, never a member of this type).
/// </summary>
public enum BrowserHostTopology
{
    /// <summary>The host's immediate parent process IS the browser (browser -&gt; host).</summary>
    Direct = 1,

    /// <summary>The host's immediate parent is the exact System32 cmd.exe, and the browser is that
    /// cmd's own parent (browser -&gt; cmd.exe -&gt; host). No deeper hop is ever recognized.</summary>
    CommandIntermediary = 2,
}
