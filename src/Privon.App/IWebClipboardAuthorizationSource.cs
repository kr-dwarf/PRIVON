using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F5A.2 (frozen contract) / Gate 031F5C (this type) -- the single Phase-E
/// plug point <see cref="ClipboardAuthorizationRouter"/> consults for a target
/// <see cref="TargetGate"/> does not already recognize. Exactly one method, exactly one parameter,
/// <see cref="Task"/>-returning (never <c>ValueTask</c>, matching every other async seam in this
/// codebase) -- no <see cref="System.Threading.CancellationToken"/>, matching the frozen
/// no-cancellation-token convention this codebase already locks for its other async transport
/// seams, and no synchronous/Try-style overload.
///
/// ASYNC_IS_REAL (Gate 031F5A.2): a real Phase-E implementation performs a decision-time Native
/// Messaging challenge/response before a result exists, so this method's returned <see cref="Task"/>
/// may genuinely remain incomplete for as long as that exchange takes. Neither
/// <see cref="ClipboardPrivacyCoordinator"/> nor <see cref="ClipboardDecisionActionResolver"/> nor
/// <see cref="ClipboardAuthorizationRouter"/> may block on it -- only await it.
///
/// TIMEOUT_OWNERSHIP (Gate 031F5A.2): a bounded wait, if any, belongs entirely to the Phase-E
/// implementation of this interface. Neither this interface nor any of its callers knows or owns a
/// timeout value.
///
/// <see langword="null"/> means an ordinary, expected decline (not authorized, unavailable, or a
/// challenge that did not complete in time) -- never an exception. An implementation defect is
/// still free to throw or fault the returned <see cref="Task"/>; see
/// <see cref="ClipboardAuthorizationRouter.AuthorizeAsync"/>'s own doc for why that is deliberately
/// allowed to propagate rather than being reinterpreted as a decline.
///
/// Phase D implements NO concrete production type over this interface -- see this type's own
/// PRODUCTION_DEFAULT doc on <see cref="ClipboardAuthorizationRouter"/>. Only a future Phase-E
/// Native Messaging authorization component will.
/// </summary>
internal interface IWebClipboardAuthorizationSource
{
    Task<WebClipboardAuthorization?> AuthorizeAsync(ForegroundTargetSnapshot foreground);
}
