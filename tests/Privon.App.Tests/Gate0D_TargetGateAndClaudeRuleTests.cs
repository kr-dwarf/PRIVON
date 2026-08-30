using System.Reflection;
using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.0 Gate 0D -- RED-only coverage for the typed multi-target result and the Claude
// Windows positive authorization rule (Windows Multi-AI Authorization Contract Freeze, Gate 0B).
// TargetGate.Match and SupportedTarget do not exist in the current 0.2.1 production source --
// every test below locates them via reflection so the assembly still builds against the unchanged
// production code, and fails with an explicit, case-specific assertion (never a compiler error).
//
// Existing coverage this file deliberately does NOT duplicate: the full ChatGPT positive/negative
// matrix (official PFN, ChatGPT Classic, wrong PFN, unresolved package, unpackaged ChatGPT-shaped
// executable) already lives in TargetGateTests.cs and Bug004TargetIdentityTests.cs and remains
// the authority for that target -- see Gate 0D section 3. Nothing here re-asserts it.
public class Gate0D_TargetGateAndClaudeRuleTests
{
    private const string ApprovedChatGptPfn = "OpenAI.Codex_2p2nqsd0c76g0";
    private const string AnthropicOrganization = "Anthropic, PBC";

    private static Assembly AppAssembly => typeof(TargetGate).Assembly;

    private static Type? FindType(string simpleName) =>
        AppAssembly.GetTypes().FirstOrDefault(t => t.Name == simpleName);

    // ==================================================================
    // Section 2 -- TYPED TARGET RED
    // ==================================================================

    // ---- Gate0D-T1: SupportedTarget must exist as an enum with EXACTLY {ChatGpt, Claude} -- this
    // single assertion also structurally proves section 5's requirement that no Antigravity (or any
    // third) target member can exist in 0.3.0: the set is closed, not merely "doesn't currently
    // include Antigravity". ----
    [Fact]
    public void Gate0D_T1_SupportedTarget_IsEnumWithExactlyChatGptAndClaude()
    {
        var type = FindType("SupportedTarget");
        Assert.True(type is not null,
            "Privon.App must define SupportedTarget as a typed multi-target authorization result " +
            "(Gate 0D TARGET_RESULT) -- not present in the current 0.2.1 production source, which " +
            "only exposes TargetGate.IsSupportedTarget(bool).");

        Assert.True(type!.IsEnum, "SupportedTarget must be an enum.");

        var actual = Enum.GetNames(type).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var expected = new[] { "ChatGpt", "Claude" }.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    // ---- Gate0D-T2: no member may carry the value 0 -- so no default(SupportedTarget) can ever be
    // mistaken for a supported target (Gate 0D TARGET_RESULT: "no default enum value may mean
    // supported"). ----
    [Fact]
    public void Gate0D_T2_SupportedTarget_HasNoZeroValuedMember()
    {
        var type = FindType("SupportedTarget");
        Assert.True(type is not null,
            "Cannot verify the zero-value contract because SupportedTarget does not exist yet -- " +
            "see Gate0D-T1.");

        var underlyingValues = Enum.GetValues(type!).Cast<object>()
            .Select(v => Convert.ToInt64(v))
            .ToArray();

        Assert.DoesNotContain(0L, underlyingValues);
    }

    // ---- Gate0D-T3: TargetGate must expose a typed matching operation -- SupportedTarget? Match
    // (ForegroundTargetSnapshot) -- whose unsupported result is absence (null), never a sentinel
    // enum member, so it structurally cannot be confused with a supported target. ----
    [Fact]
    public void Gate0D_T3_TargetGate_ExposesTypedMatchOperation()
    {
        bool found = TryFindMatchMethod(out _, out string reason);
        Assert.True(found, reason);
    }

    private static bool TryFindMatchMethod(out MethodInfo? method, out string reason)
    {
        method = null;
        var supportedTargetType = FindType("SupportedTarget");
        if (supportedTargetType is null)
        {
            reason = "TargetGate.Match cannot be located because SupportedTarget does not exist " +
                "yet -- see Gate0D-T1.";
            return false;
        }

        var nullableType = typeof(Nullable<>).MakeGenericType(supportedTargetType);
        method = typeof(TargetGate)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.ReturnType == nullableType
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(ForegroundTargetSnapshot));

        if (method is null)
        {
            reason = "TargetGate must expose a typed matching operation shaped " +
                "'SupportedTarget? Match(ForegroundTargetSnapshot)' (Gate 0D TARGET_RESULT) -- " +
                "not present yet; only the existing bool IsSupportedTarget exists in 0.2.1.";
            return false;
        }

        reason = "";
        return true;
    }

    // ---- Gate0D-T4: the ChatGPT positive case (already proven boolean-true by TargetGateTests.cs)
    // must resolve through the NEW typed operation to exactly SupportedTarget.ChatGpt -- proving the
    // typed result is not merely present but semantically correct for the existing target. ----
    [Fact]
    public void Gate0D_T4_Match_OfficialChatGptIdentity_ReturnsExactlyChatGpt()
    {
        AssertMatchesExactly(
            BuildChatGptSnapshot(),
            "ChatGpt",
            "Gate 0D T4 (typed ChatGPT positive case)");
    }

    // ---- Gate0D-T5: an unknown foreground application must resolve through Match to no target at
    // all (null), never a sentinel/default enum value. ----
    [Fact]
    public void Gate0D_T5_Match_UnknownApplication_ReturnsNoTarget()
    {
        var snapshot = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "notepad");
        AssertMatchesNoTarget(snapshot, "Gate 0D T5 (unknown application)");
    }

    private static ForegroundTargetSnapshot BuildChatGptSnapshot() =>
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: ApprovedChatGptPfn);

    // ==================================================================
    // Shared Claude-snapshot construction (Section 4) -- via reflection over
    // ForegroundTargetSnapshot's constructor, since ExecutableSignature/SignerOrganization do not
    // exist as named parameters yet (same technique as
    // Gate0D_ExecutableSignatureFactModelTests.TryBuildSnapshot in Privon.Windows.IntegrationTests,
    // duplicated here because it is a separate test assembly with no shared test-utility project).
    // ==================================================================

    private static bool TryBuildClaudeCandidateSnapshot(
        string processName,
        PackageIdentityResolution packageIdentity,
        string executableSignatureStateName,
        string? signerOrganization,
        out ForegroundTargetSnapshot snapshot,
        out string failureReason)
    {
        snapshot = default;

        var enumType = typeof(ForegroundTargetSnapshot).Assembly.GetTypes()
            .FirstOrDefault(t => t.Name == "ExecutableSignatureResolution");

        if (enumType is null)
        {
            failureReason = "ExecutableSignatureResolution does not exist yet in Privon.Windows " +
                "(see the fact-model RED suite in Privon.Windows.IntegrationTests).";
            return false;
        }

        var ctor = typeof(ForegroundTargetSnapshot).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Any(p => p.Name == "ExecutableSignature")
                && c.GetParameters().Any(p => p.Name == "SignerOrganization"));
        if (ctor is null)
        {
            failureReason = "ForegroundTargetSnapshot's constructor does not yet accept " +
                "ExecutableSignature/SignerOrganization.";
            return false;
        }

        object executableSignatureValue = Enum.Parse(enumType, executableSignatureStateName);
        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            args[i] = parameters[i].Name switch
            {
                "IsResolved" => true,
                "ProcessId" => (uint)4242,
                "ProcessName" => processName,
                "PackageIdentity" => packageIdentity,
                "PackageFamilyName" => (string?)null,
                "ExecutableSignature" => executableSignatureValue,
                "SignerOrganization" => signerOrganization,
                _ => parameters[i].HasDefaultValue
                    ? parameters[i].DefaultValue
                    : (parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null),
            };
        }

        snapshot = (ForegroundTargetSnapshot)ctor.Invoke(args)!;
        failureReason = "";
        return true;
    }

    private static void AssertMatchesExactly(ForegroundTargetSnapshot snapshot, string expectedMemberName, string caseLabel)
    {
        bool foundMethod = TryFindMatchMethod(out var method, out string reason);
        Assert.True(foundMethod, caseLabel + ": " + reason);

        object? result = method!.Invoke(null, new object[] { snapshot });

        Assert.True(result is not null && result.ToString() == expectedMemberName,
            caseLabel + ": expected TargetGate.Match to return SupportedTarget." + expectedMemberName +
            ", got " + (result?.ToString() ?? "null") + ".");
    }

    private static void AssertMatchesNoTarget(ForegroundTargetSnapshot snapshot, string caseLabel)
    {
        bool foundMethod = TryFindMatchMethod(out var method, out string reason);
        Assert.True(foundMethod, caseLabel + ": " + reason);

        object? result = method!.Invoke(null, new object[] { snapshot });

        Assert.True(result is null,
            caseLabel + ": expected TargetGate.Match to return no target (null), got " +
            (result?.ToString() ?? "null") + ".");
    }

    // ==================================================================
    // Section 4 -- CLAUDE AUTHORIZATION RED (cases A-L)
    // ==================================================================

    // ---- Case A: claude + NoPackage + Trusted + exact O="Anthropic, PBC" -> Claude. ----
    [Fact]
    public void Gate0D_Case_A_ValidAnthropicSignedClaude_MatchesClaude()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.NoPackage,
            "Trusted", AnthropicOrganization, out var snapshot, out string reason);
        Assert.True(built, "Case A: " + reason);

        AssertMatchesExactly(snapshot, "Claude", "Case A (valid Anthropic-signed Claude)");
    }

    // ---- Case B: same-name unsigned executable -> unsupported. No signature at all: Untrusted
    // state, no signer organization to report. ----
    [Fact]
    public void Gate0D_Case_B_UnsignedSameNameExecutable_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.NoPackage,
            "Untrusted", null, out var snapshot, out string reason);
        Assert.True(built, "Case B: " + reason);

        AssertMatchesNoTarget(snapshot, "Case B (unsigned same-name executable)");
    }

    // ---- Case C: self-signed executable FALSELY CLAIMING SignerOrganization="Anthropic, PBC" in
    // its (untrusted-chain) certificate subject -> unsupported. Proves ExecutableSignature==Trusted
    // is checked as a genuine gate, not merely alongside SignerOrganization -- an attacker cannot
    // win by forging the subject field of a certificate that never chains to a trusted root. ----
    [Fact]
    public void Gate0D_Case_C_SelfSignedClaimingAnthropicOrganization_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.NoPackage,
            "Untrusted", AnthropicOrganization, out var snapshot, out string reason);
        Assert.True(built, "Case C: " + reason);

        AssertMatchesNoTarget(snapshot, "Case C (self-signed, chain untrusted, forged subject)");
    }

    // ---- Case D: validly trusted signer, but a DIFFERENT publisher (O="Google LLC") ->
    // unsupported. Also the exact structural shape a real Antigravity binary would present if
    // Google ever ships a signed unpackaged Windows build -- see the dedicated Antigravity tests
    // below for the full section-5 exclusion. ----
    [Fact]
    public void Gate0D_Case_D_TrustedButWrongPublisher_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.NoPackage,
            "Trusted", "Google LLC", out var snapshot, out string reason);
        Assert.True(built, "Case D: " + reason);

        AssertMatchesNoTarget(snapshot, "Case D (trusted signature, wrong publisher)");
    }

    // ---- Case E: signer lookup was attempted but inconclusive (trust-API error, offline chain
    // build failure, etc.) -> unsupported. Fails closed, never treated as "close enough". ----
    [Fact]
    public void Gate0D_Case_E_SignatureResolutionUnresolved_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.NoPackage,
            "Unresolved", AnthropicOrganization, out var snapshot, out string reason);
        Assert.True(built, "Case E: " + reason);

        AssertMatchesNoTarget(snapshot, "Case E (signature lookup unresolved)");
    }

    // ---- Case F: signature inspection was never even performed (NotInspected, the safe default)
    // -> unsupported. Distinct from Case E: "never looked" must fail exactly as closed as "looked
    // and could not tell". ----
    [Fact]
    public void Gate0D_Case_F_SignatureNotInspected_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.NoPackage,
            "NotInspected", null, out var snapshot, out string reason);
        Assert.True(built, "Case F: " + reason);

        AssertMatchesNoTarget(snapshot, "Case F (signature never inspected)");
    }

    // ---- Case G: correct signer evidence, but PackageIdentity == Resolved (a packaged impostor
    // masquerading with Claude's process name and a trusted-but-irrelevant signature) ->
    // unsupported. Proves the Claude rule requires NoPackage as a genuine gate, keeping the ChatGPT
    // and Claude rules structurally mutually exclusive. ----
    [Fact]
    public void Gate0D_Case_G_TrustedSignerButPackaged_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.Resolved,
            "Trusted", AnthropicOrganization, out var snapshot, out string reason);
        Assert.True(built, "Case G: " + reason);

        AssertMatchesNoTarget(snapshot, "Case G (trusted signer, but package identity resolved)");
    }

    // ---- Case H: correct signer evidence, but PackageIdentity == Unresolved (package inspection
    // itself was inconclusive) -> unsupported. Only a DEFINITIVE NoPackage satisfies the rule. ----
    [Fact]
    public void Gate0D_Case_H_TrustedSignerButPackageUnresolved_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.Unresolved,
            "Trusted", AnthropicOrganization, out var snapshot, out string reason);
        Assert.True(built, "Case H: " + reason);

        AssertMatchesNoTarget(snapshot, "Case H (trusted signer, but package identity unresolved)");
    }

    // ---- Case I: organization case variation ("anthropic, pbc") -> unsupported. SignerOrganization
    // must compare Ordinal, never case-folded -- a certificate subject Organization attribute is an
    // exact identifier. ----
    [Fact]
    public void Gate0D_Case_I_OrganizationCaseVariation_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.NoPackage,
            "Trusted", "anthropic, pbc", out var snapshot, out string reason);
        Assert.True(built, "Case I: " + reason);

        AssertMatchesNoTarget(snapshot, "Case I (organization case variation)");
    }

    // ---- Case J: organization whitespace variation (" Anthropic, PBC ") -> unsupported. Never
    // trimmed. ----
    [Fact]
    public void Gate0D_Case_J_OrganizationWhitespaceVariation_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.NoPackage,
            "Trusted", " Anthropic, PBC ", out var snapshot, out string reason);
        Assert.True(built, "Case J: " + reason);

        AssertMatchesNoTarget(snapshot, "Case J (organization whitespace variation)");
    }

    // ---- Case K: name/path-shaped Claude ("claude", unpackaged) with NO signer evidence of any
    // kind (default ExecutableSignature, null organization) -> unsupported. Proves the process-name
    // pre-filter alone is exactly as insufficient for Claude as it was proven insufficient for
    // ChatGPT (BUG-004 Gate 2G) -- name/path alone can never authorize. ----
    [Fact]
    public void Gate0D_Case_K_NameShapedClaudeWithNoSignerEvidence_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("claude", PackageIdentityResolution.NoPackage,
            "NotInspected", null, out var snapshot, out string reason);
        Assert.True(built, "Case K: " + reason);

        AssertMatchesNoTarget(snapshot, "Case K (name-shaped Claude, no signer evidence)");
    }

    // ---- Case L: certificate rotation is irrelevant to authorization because the fact model
    // structurally cannot express a thumbprint/serial/issuer/validity window -- there is nothing
    // for TargetGate to accidentally pin. Verified here as a source-text guard on TargetGate.cs
    // itself (forward regression lock; already holds today). ----
    [Fact]
    public void Gate0D_Case_L_TargetGateSource_NeverReferencesCertificateRotationDetails()
    {
        string source = File.ReadAllText(FindAppSourceFile("TargetGate.cs"));
        string[] forbidden = { "Thumbprint", "Serial", "Issuer", "NotBefore", "NotAfter" };

        foreach (string token in forbidden)
        {
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ==================================================================
    // Section 5 -- ANTIGRAVITY STRUCTURAL EXCLUSION
    // ==================================================================

    // ---- Antigravity, maximally favorable: unpackaged, validly Authenticode-signed by its real
    // publisher (Google LLC) -- still must resolve to NO target. Not merely "not authorized as
    // Claude" (Case D already proves that under the name "claude") -- here the process name is
    // genuinely "Antigravity", proving there is no name-agnostic fallback path either. ----
    [Fact]
    public void Gate0D_Antigravity_MaximallyFavorableTrustedGoogleSignedExecutable_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("Antigravity", PackageIdentityResolution.NoPackage,
            "Trusted", "Google LLC", out var snapshot, out string reason);
        Assert.True(built, "Antigravity case: " + reason);

        AssertMatchesNoTarget(snapshot, "Antigravity (trusted, real Google signature, unpackaged)");
    }

    // ---- Antigravity cannot win even with a SPOOFED/forged SignerOrganization string claiming
    // "Anthropic, PBC" -- the process-name pre-filter must still reject it, proving there is no
    // implicit "any Anthropic-signed process" fallback that a differently-named binary could ride
    // in on. ----
    [Fact]
    public void Gate0D_Antigravity_SpoofedAnthropicOrganizationString_IsUnsupported()
    {
        bool built = TryBuildClaudeCandidateSnapshot("Antigravity", PackageIdentityResolution.NoPackage,
            "Trusted", AnthropicOrganization, out var snapshot, out string reason);
        Assert.True(built, "Antigravity spoofed-organization case: " + reason);

        AssertMatchesNoTarget(snapshot, "Antigravity (spoofed Anthropic organization string)");
    }

    // ---- No enum member named "Antigravity" (or anything else) may exist on SupportedTarget --
    // the two-entry set is closed, not merely empirically unauthorized today. Structural companion
    // to the two behavioral tests above. ----
    [Fact]
    public void Gate0D_Antigravity_NoSupportedTargetMemberExists()
    {
        var type = FindType("SupportedTarget");
        Assert.True(type is not null,
            "Cannot verify the closed target set because SupportedTarget does not exist yet -- see " +
            "Gate0D-T1.");

        var names = Enum.GetNames(type!);
        Assert.DoesNotContain(names, n => n.Contains("Antigravity", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindAppSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate src/Privon.App from the test output directory.");

        return Path.Combine(dir.FullName, "src", "Privon.App", fileName);
    }
}
