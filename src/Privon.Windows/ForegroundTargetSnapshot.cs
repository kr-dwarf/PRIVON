namespace Privon.Windows;

/// <summary>
/// Phase 3A.5 STEP2 -- a single mechanical fact about the current foreground window/process,
/// nothing more. Deliberately carries no product/privacy policy: no notion of "supported target,"
/// "ChatGPT," "eligible," or "protected" exists anywhere in this type or the rest of
/// <c>Privon.Windows</c> -- that judgment belongs entirely to a future <c>Privon.App</c>-owned
/// TargetGate, which is the only thing that ever compares <see cref="ProcessName"/> against a
/// configured supported-target name.
///
/// <see cref="IsResolved"/> is <c>false</c> for every ordinary, expected condition: no foreground
/// window, an invalid HWND/PID, or a process that exited between capturing its PID and looking up
/// its name (a real, expected race -- never a crash). <see cref="ProcessId"/>/<see cref="ProcessName"/>
/// are only meaningful when <see cref="IsResolved"/> is <c>true</c>; the type's own default value
/// (<c>default(ForegroundTargetSnapshot)</c>) already has <see cref="IsResolved"/> false (bool's
/// own default), <see cref="ProcessId"/> 0, and <see cref="ProcessName"/> null -- so a caller that
/// forgets to check <see cref="IsResolved"/> or receives a default-initialized value can never
/// mistake it for a resolved target.
///
/// <see cref="ProcessName"/> is exactly what <c>System.Diagnostics.Process.ProcessName</c> returns
/// for the resolved PID -- the executable's base name without a path or the ".exe" extension, per
/// that property's own documented BCL semantics. No normalization, no case-folding is applied here
/// (a future TargetGate decides its own comparison semantics).
/// </summary>
public readonly record struct ForegroundTargetSnapshot(bool IsResolved, uint ProcessId, string? ProcessName);
