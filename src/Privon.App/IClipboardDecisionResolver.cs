using Privon.Core;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- narrow seam over <see cref="ClipboardDecisionActionResolver"/>'s own
/// <c>ResolveAsync</c>, existing solely so <see cref="DecisionPromptCoordinator"/> can be tested
/// against a hand-written fake without needing the resolver's full dependency graph (operation
/// gate/lifecycle/target capture/read+write transport/processor/verification handoff) -- the same
/// narrow-seam-over-a-richer-concern pattern already used throughout this project for
/// <see cref="IClipboardReadTransport"/>/<see cref="IClipboardPrivacyProcessor"/>/
/// <see cref="IClipboardDecisionSessionPublisher"/>. <see cref="ClipboardDecisionActionResolver"/>
/// is the only production implementation -- this interface changes none of its own behavior,
/// dependencies, or ERROR_BOUNDARY/STEP24.1 linearization contract.
/// </summary>
internal interface IClipboardDecisionResolver
{
    Task<ClipboardDecisionActionResult> ResolveAsync(
        ClipboardDecisionScope scope, ClipboardDecisionItem item, ClipboardDecisionIntent intent);
}
