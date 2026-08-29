namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3B -- narrow App-owned seam over the Storage &lt;-&gt; user-exception bridge,
/// mirroring <see cref="ITrustExceptionProvider"/>/<see cref="IProtectionCategorySettingsProvider"/>'s
/// own established shape exactly. Synchronous, same rationale as those two.
///
/// Exists so App-level orchestration can be tested with a hand-written fake instead of a real
/// <c>PrivonLocalStore</c>, without widening <c>Privon.Storage</c>'s own public surface.
/// </summary>
internal interface IUserExceptionProvider
{
    /// <summary>
    /// Loads the current set of user-registered exact-value exceptions -- freshly, every call.
    /// No App-side cache, matching <see cref="ITrustExceptionProvider.Load"/>/
    /// <see cref="IProtectionCategorySettingsProvider.Load"/>'s own CACHE_POLICY: an exception
    /// added or deleted must take effect on the very next clipboard attempt. Never returns a
    /// partially-populated or null value -- always a complete (possibly empty) list, resolved to
    /// empty whenever Storage itself would have (missing file, corruption, unreadable -- see
    /// <c>PrivonLocalStore.LoadUserExceptions</c>'s own fail-safe doc). Missing/corrupt/unreadable
    /// storage therefore always means NO exception is granted -- ordinary protection resumes,
    /// never the other way around.
    /// </summary>
    IReadOnlyList<UserExceptionValue> Load();
}
