using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.0 Gate 0D -- RED-only fact-model coverage for the executable-signature mechanical
// facts the Claude Windows authorization rule needs (Windows Multi-AI Authorization Contract
// Freeze, Gate 0B, "CLAUDE_RULE"/"FACT_MODEL" sections). NONE of the production types this file
// probes exist yet in 0.2.1 -- every test below uses reflection/structural inspection specifically
// so the test ASSEMBLY still builds against the unchanged 0.2.1 production source, and each test
// fails with an explicit, case-specific assertion rather than a compiler error (per Gate 0D's own
// RED-test rule: "a RED caused merely by 'CSxxxx type/member does not exist' is NOT acceptable
// evidence").
//
// SCOPE: this file only ever asserts what the FACT MODEL (Privon.Windows) must be able to
// represent, and (Gate 0D section 9) that ForegroundIdentityCapture.Matches must incorporate the
// new facts once they exist. It asserts NOTHING about which signer/publisher is "supported" --
// that remains TargetGate's exclusive concern (see Gate0D_TargetGateAndClaudeRuleTests.cs in
// Privon.App.Tests), mirroring PackageIdentityFactTests.cs's own existing FACTS_ONLY discipline.
public class Gate0D_ExecutableSignatureFactModelTests
{
    private static Assembly WindowsAssembly => typeof(ForegroundTargetSnapshot).Assembly;

    private static Type? FindType(string simpleName) =>
        WindowsAssembly.GetTypes().FirstOrDefault(t => t.Name == simpleName);

    // ==================================================================
    // Section 1 -- FACT MODEL RED
    // ==================================================================

    // ---- Gate0D-1: the new tri-...four-state mechanical fact enum must exist. ----
    [Fact]
    public void Gate0D_1_ExecutableSignatureResolution_TypeExists()
    {
        var type = FindType("ExecutableSignatureResolution");

        Assert.True(type is not null,
            "Privon.Windows must define ExecutableSignatureResolution -- a mechanical, product-" +
            "policy-free enum reporting whether a foreground process's executable carries a " +
            "verifiable trusted Authenticode signature (Gate 0D CLAUDE_RULE / FACT_MODEL). Not " +
            "present in the current 0.2.1 fact model.");
    }

    // ---- Gate0D-2: exactly the four required semantic states, matching PackageIdentityResolution's
    // own precedent of a tri-state (here four-state) fact that never conflates "did not look" with
    // "looked and could not tell" with "looked and it failed" with "looked and it is trusted". ----
    [Fact]
    public void Gate0D_2_ExecutableSignatureResolution_HasExactlyRequiredStates()
    {
        var type = FindType("ExecutableSignatureResolution");
        Assert.True(type is not null,
            "Cannot verify ExecutableSignatureResolution's four required states (NotInspected, " +
            "Unresolved, Untrusted, Trusted) because the type does not exist yet -- see Gate0D-1.");

        Assert.True(type!.IsEnum, "ExecutableSignatureResolution must be an enum, matching " +
            "PackageIdentityResolution's own established fact-enum shape.");

        var actual = Enum.GetNames(type).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var expected = new[] { "NotInspected", "Trusted", "Unresolved", "Untrusted" }
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    // ---- Gate0D-3: the safe default (0) must be NotInspected -- a default-initialized
    // ForegroundTargetSnapshot, or one from any pre-Gate-0D construction site that never mentions
    // this new field, must never be silently mistaken for "inspected and found untrusted" or
    // "inspected and confirmed trusted". Mirrors PackageIdentityResolution's own "safe value first"
    // discipline (Unresolved declared first there; NotInspected is the analogous safe default here,
    // distinct from Unresolved because "never inspected" -- e.g. a packaged process -- must not be
    // conflated with "inspected but inconclusive"). ----
    [Fact]
    public void Gate0D_3_ExecutableSignatureResolution_DefaultValueIsNotInspected()
    {
        var type = FindType("ExecutableSignatureResolution");
        Assert.True(type is not null,
            "Cannot verify the safe default state because ExecutableSignatureResolution does not " +
            "exist yet -- see Gate0D-1.");

        object defaultValue = Activator.CreateInstance(type!)!;
        Assert.Equal("NotInspected", defaultValue.ToString());
    }

    // ---- Gate0D-4: ForegroundTargetSnapshot must gain an ExecutableSignature property of exactly
    // the new enum's type -- the mechanical carrier TargetGate's future Claude rule reads. ----
    [Fact]
    public void Gate0D_4_ForegroundTargetSnapshot_ExposesExecutableSignatureProperty()
    {
        var enumType = FindType("ExecutableSignatureResolution");
        var prop = typeof(ForegroundTargetSnapshot).GetProperty(
            "ExecutableSignature", BindingFlags.Public | BindingFlags.Instance);

        Assert.True(prop is not null,
            "ForegroundTargetSnapshot must gain a public ExecutableSignature property (Gate 0D " +
            "FACT_MODEL) carrying the Authenticode verification mechanical fact required by " +
            "TargetGate's Claude rule. Not present in the current 0.2.1 snapshot shape.");

        Assert.True(enumType is not null && prop!.PropertyType == enumType,
            "ForegroundTargetSnapshot.ExecutableSignature must be typed as " +
            "ExecutableSignatureResolution, not some other type.");
    }

    // ---- Gate0D-5: ForegroundTargetSnapshot must gain a SignerOrganization string property -- the
    // supporting fact (Gate 0D CLAUDE_RULE) alongside ExecutableSignature. Deliberately a bare
    // string, never a structured certificate-subject type (this fact model must not be able to
    // express a thumbprint/serial/issuer -- see Gate0D-6 below). ----
    [Fact]
    public void Gate0D_5_ForegroundTargetSnapshot_ExposesSignerOrganizationStringProperty()
    {
        var prop = typeof(ForegroundTargetSnapshot).GetProperty(
            "SignerOrganization", BindingFlags.Public | BindingFlags.Instance);

        Assert.True(prop is not null,
            "ForegroundTargetSnapshot must gain a public SignerOrganization property (Gate 0D " +
            "CLAUDE_RULE) -- the Authenticode subject Organization (O=) mechanical fact. Not " +
            "present in the current 0.2.1 snapshot shape.");

        Assert.Equal(typeof(string), prop!.PropertyType);
    }

    // ==================================================================
    // Section 9 -- EXECUTION GUARD RED (Matches-level unit coverage).
    //
    // Full pipeline-level coverage (ClipboardChangeMonitor/ComposerTextReader rejecting a live
    // guarded read/write via a FAKE IForegroundTargetSource reporting a different signer) requires
    // widening IForegroundTargetSource.TryResolveConfirmedForegroundIdentity's own out-parameters --
    // a second production interface change beyond the fact model above. That is deliberately NOT
    // attempted here (would require editing FakeForegroundTargetSource's production-interface-bound
    // signature, which cannot compile against the unchanged interface): DEFERRED_TO_GREEN_SEAM,
    // covered once IForegroundTargetSource itself widens in the implementation gate. What IS
    // testable now, compile-safely, is ForegroundIdentityCapture.Matches's own comparison contract,
    // exercised directly with two snapshots built via reflection over ForegroundTargetSnapshot's
    // constructor (never via a hand-written object initializer, since the new named parameters do
    // not exist yet either).
    // ==================================================================

    private static bool TryBuildSnapshot(
        bool isResolved,
        uint processId,
        string? processName,
        PackageIdentityResolution packageIdentity,
        string? packageFamilyName,
        string executableSignatureStateName,
        string? signerOrganization,
        out ForegroundTargetSnapshot snapshot,
        out string failureReason)
    {
        snapshot = default;

        var enumType = FindType("ExecutableSignatureResolution");
        if (enumType is null)
        {
            failureReason = "ExecutableSignatureResolution does not exist yet (see Gate0D-1).";
            return false;
        }

        var ctor = typeof(ForegroundTargetSnapshot).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Any(p => p.Name == "ExecutableSignature")
                && c.GetParameters().Any(p => p.Name == "SignerOrganization"));
        if (ctor is null)
        {
            failureReason = "ForegroundTargetSnapshot's constructor does not yet accept " +
                "ExecutableSignature/SignerOrganization (see Gate0D-4/Gate0D-5).";
            return false;
        }

        object executableSignatureValue = Enum.Parse(enumType, executableSignatureStateName);
        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            args[i] = parameters[i].Name switch
            {
                "IsResolved" => isResolved,
                "ProcessId" => processId,
                "ProcessName" => processName,
                "PackageIdentity" => packageIdentity,
                "PackageFamilyName" => packageFamilyName,
                "ExecutableSignature" => executableSignatureValue,
                "SignerOrganization" => signerOrganization,
                _ => parameters[i].HasDefaultValue
                    ? parameters[i].DefaultValue
                    : GetTrivialDefault(parameters[i].ParameterType),
            };
        }

        snapshot = (ForegroundTargetSnapshot)ctor.Invoke(args)!;
        failureReason = "";
        return true;
    }

    private static object? GetTrivialDefault(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

    // ---- Gate0D-9a: an unpackaged Claude-shaped snapshot whose current signature resolution
    // differs from the expected authorized one must never be treated as matching -- a previously
    // authorized process that loses its verified-trusted status (e.g. a fresh capture now reports
    // Untrusted/Unresolved) must not keep permitting raw clipboard access. ----
    [Fact]
    public void Gate0D_9a_Matches_ExecutableSignatureMismatch_MustNotMatch()
    {
        bool builtExpected = TryBuildSnapshot(true, 4242, "claude", PackageIdentityResolution.NoPackage,
            null, "Trusted", "Anthropic, PBC", out var expected, out string reasonExpected);
        bool builtCurrent = TryBuildSnapshot(true, 4242, "claude", PackageIdentityResolution.NoPackage,
            null, "Untrusted", "Anthropic, PBC", out var current, out string reasonCurrent);

        Assert.True(builtExpected && builtCurrent,
            "Gate 0D section 9.1 (execution-guard ExecutableSignature mismatch) cannot yet be " +
            "expressed: " + (builtExpected ? reasonCurrent : reasonExpected));

        bool matches = ForegroundIdentityCapture.Matches(current, expected);

        Assert.False(matches,
            "ForegroundIdentityCapture.Matches must reject a current snapshot whose " +
            "ExecutableSignature differs from the expected authorized one (Trusted vs Untrusted), " +
            "even when PID/ProcessName/PackageIdentity all still agree.");
    }

    // ---- Gate0D-9b: same contract, for a SignerOrganization mismatch (e.g. the process was
    // replaced by a same-PID, same-name, still-Trusted-signed executable from a DIFFERENT
    // publisher) -- the exact scenario TARGET_PROCESS_NAME_SPOOFING/BUG004-TOCTOU-001 already
    // motivated for the package-identity facts, now applied to the signer facts. ----
    [Fact]
    public void Gate0D_9b_Matches_SignerOrganizationMismatch_MustNotMatch()
    {
        bool builtExpected = TryBuildSnapshot(true, 4242, "claude", PackageIdentityResolution.NoPackage,
            null, "Trusted", "Anthropic, PBC", out var expected, out string reasonExpected);
        bool builtCurrent = TryBuildSnapshot(true, 4242, "claude", PackageIdentityResolution.NoPackage,
            null, "Trusted", "Google LLC", out var current, out string reasonCurrent);

        Assert.True(builtExpected && builtCurrent,
            "Gate 0D section 9.2 (execution-guard SignerOrganization mismatch) cannot yet be " +
            "expressed: " + (builtExpected ? reasonCurrent : reasonExpected));

        bool matches = ForegroundIdentityCapture.Matches(current, expected);

        Assert.False(matches,
            "ForegroundIdentityCapture.Matches must reject a current snapshot whose " +
            "SignerOrganization differs from the expected authorized one (\"Anthropic, PBC\" vs " +
            "\"Google LLC\"), even when PID/ProcessName/PackageIdentity/ExecutableSignature all " +
            "still agree -- an authorized Claude session must not survive a publisher swap.");
    }

    // ---- Gate0D-9c: identical facts on both sides (including the two new ones) must still match --
    // proves the widened comparison is not merely "always false", and that legitimate re-checks of
    // the SAME still-current process keep succeeding. ----
    [Fact]
    public void Gate0D_9c_Matches_IdenticalSignerFactsOnBothSides_StillMatches()
    {
        bool builtExpected = TryBuildSnapshot(true, 4242, "claude", PackageIdentityResolution.NoPackage,
            null, "Trusted", "Anthropic, PBC", out var expected, out string reasonExpected);
        bool builtCurrent = TryBuildSnapshot(true, 4242, "claude", PackageIdentityResolution.NoPackage,
            null, "Trusted", "Anthropic, PBC", out var current, out string reasonCurrent);

        Assert.True(builtExpected && builtCurrent,
            "Gate 0D section 9.3 (execution-guard positive re-check) cannot yet be expressed: " +
            (builtExpected ? reasonCurrent : reasonExpected));

        bool matches = ForegroundIdentityCapture.Matches(current, expected);

        Assert.True(matches,
            "ForegroundIdentityCapture.Matches must still succeed when every fact -- including the " +
            "two new signer facts -- agrees on both sides.");
    }
}
