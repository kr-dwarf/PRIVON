using System.Reflection;
using Privon.Detection;

namespace Privon.Detection.Tests;

// Phase 3B STEP5.1 -- Trusted/Exception Sensitive Diagnostic Surface Hardening regression.
// TRUST_VALUE_DIAGNOSTIC_SURFACE: neither TrustedPublicValue nor AmbiguousExceptionValue's
// ToString() (nor any interpolation of one) may ever expose CanonicalValue/CanonicalValue.Value
// -- only PiiType. Unlike Phase 3B STEP3.1's hardened DTOs (EvaluatedCandidate/
// CandidatePolicyDecision/AliasAssignment), these two types were previously safe only
// transitively, via CanonicalValue's own hardened override -- this regression proves each type
// is now self-contained and does not rely on that nested override staying safe.
//
// No production behavior is exercised or changed here -- ExceptionTrustedEvaluator/
// CandidatePolicyEvaluator/CanonicalValue itself are unmodified.
public class TrustValueDiagnosticSurfaceTests
{
    private const string Sentinel = "CANONICAL-STORAGE-SENTINEL-481739";

    // ==================================================================
    // 5/6. TrustedPublicValue
    // ==================================================================

    [Fact]
    public void TrustedPublicValue_ToString_DoesNotContainSentinel()
    {
        var value = new TrustedPublicValue(PiiType.Phone, new CanonicalValue(PiiType.Phone, Sentinel));

        Assert.DoesNotContain(Sentinel, value.ToString());
    }

    [Fact]
    public void TrustedPublicValue_Interpolation_DoesNotContainSentinel()
    {
        var value = new TrustedPublicValue(PiiType.Phone, new CanonicalValue(PiiType.Phone, Sentinel));

        Assert.DoesNotContain(Sentinel, $"{value}");
    }

    [Fact]
    public void TrustedPublicValue_ToString_ContainsOnlyPiiType_NeverReferencesCanonicalValue()
    {
        var value = new TrustedPublicValue(PiiType.Phone, new CanonicalValue(PiiType.Phone, Sentinel));

        string rendered = value.ToString();

        Assert.Contains(nameof(TrustedPublicValue.PiiType), rendered);
        Assert.Contains(nameof(PiiType.Phone), rendered);
        Assert.DoesNotContain(nameof(TrustedPublicValue.CanonicalValue), rendered);
        Assert.DoesNotContain("Value =", rendered);
    }

    // ==================================================================
    // 7/8. AmbiguousExceptionValue
    // ==================================================================

    [Fact]
    public void AmbiguousExceptionValue_ToString_DoesNotContainSentinel()
    {
        var value = new AmbiguousExceptionValue(PiiType.Email, new CanonicalValue(PiiType.Email, Sentinel));

        Assert.DoesNotContain(Sentinel, value.ToString());
    }

    [Fact]
    public void AmbiguousExceptionValue_Interpolation_DoesNotContainSentinel()
    {
        var value = new AmbiguousExceptionValue(PiiType.Email, new CanonicalValue(PiiType.Email, Sentinel));

        Assert.DoesNotContain(Sentinel, $"{value}");
    }

    [Fact]
    public void AmbiguousExceptionValue_ToString_ContainsOnlyPiiType_NeverReferencesCanonicalValue()
    {
        var value = new AmbiguousExceptionValue(PiiType.Email, new CanonicalValue(PiiType.Email, Sentinel));

        string rendered = value.ToString();

        Assert.Contains(nameof(AmbiguousExceptionValue.PiiType), rendered);
        Assert.Contains(nameof(PiiType.Email), rendered);
        Assert.DoesNotContain(nameof(AmbiguousExceptionValue.CanonicalValue), rendered);
        Assert.DoesNotContain("Value =", rendered);
    }

    // ==================================================================
    // Self-containment: safety does not depend on CanonicalValue's own hardened override --
    // proven structurally by confirming neither type's ToString ever mentions "CanonicalValue"
    // at all, not just by sentinel absence (a sentinel collision would not catch a regression
    // where the field name changed but still delegated to a now-unsafe nested ToString).
    // ==================================================================

    [Theory]
    [InlineData(typeof(TrustedPublicValue))]
    [InlineData(typeof(AmbiguousExceptionValue))]
    public void TrustValueTypes_HaveNoDebuggerDisplayOrTypeProxyAttributes(Type type)
    {
        var attributes = type.GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }
}
