using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP17 -- the App-owned Channel element type, replacing the bare
/// <see cref="ClipboardChangeNotification"/> the mailbox previously carried. Pairs the Windows
/// notification with the exact clipboard-attempt generation
/// <see cref="IClipboardNotificationLifecycle.AdvanceOnClipboardNotification"/> returned for it,
/// captured immutably at arrival time (Phase 3B STEP16's WHY_READING_LATER_IS_NOT_ENOUGH finding
/// -- a worker that only reads "the current generation" later cannot tell whether it is still the
/// one that applies to the item it is processing; the value must travel WITH the item).
///
/// Deliberately App-only -- <see cref="ClipboardChangeNotification"/> itself (a
/// <c>Privon.Windows</c> public type) is never mutated or extended to carry this App-layer
/// lifecycle concept, matching the same boundary discipline that keeps "ChatGPT"/<see cref="TargetGate"/>
/// out of <c>Privon.Windows</c> entirely.
///
/// No custom <c>ToString()</c> override: both fields are already safe metadata-only content --
/// <see cref="Notification"/> carries no raw clipboard text (nothing in
/// <see cref="ClipboardChangeNotification"/>'s own three value fields is content-derived), and
/// <see cref="Generation"/> is a plain counter (see <c>Privon.Core.RevisionId</c>'s identical
/// classification) -- so the compiler-synthesized record <c>ToString()</c> is already safe,
/// exactly like <see cref="ClipboardChangeNotification"/>/<c>Privon.Windows.ClipboardWriteResult</c>
/// themselves never needed a hardened override either.
/// </summary>
internal readonly record struct ClipboardDispatchItem(ClipboardChangeNotification Notification, long Generation);
