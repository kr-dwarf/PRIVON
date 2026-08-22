using System.Reflection;
using System.Text.Json;

namespace Privon.Storage.Tests;

// Phase 3B STEP5.1 -- Trusted/Exception Sensitive Diagnostic Surface Hardening regression.
// STORAGE_DTO_DIAGNOSTIC_SURFACE: neither TrustedPublicInfoEntry nor ExceptionEntry's ToString()
// (nor any interpolation of one) may ever expose Value -- only PiiTypeId. Mirrors the identical
// Privon.Detection.Tests.DetectionSensitiveDiagnosticSurfaceTests / Privon.Windows.IntegrationTests.
// ClipboardDiagnosticSurfaceTests precedent (Phase 3B STEP3.1 / Phase 3A.4 STEP3.2). Synthetic
// canonical sentinel only -- no real PII, no real user files.
public class StorageDtoDiagnosticSurfaceTests : IDisposable
{
    private const string Sentinel = "CANONICAL-STORAGE-SENTINEL-481739";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonStorageDiagnosticTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ==================================================================
    // 1/2. TrustedPublicInfoEntry
    // ==================================================================

    [Fact]
    public void TrustedPublicInfoEntry_ToString_DoesNotContainSentinel()
    {
        var entry = new TrustedPublicInfoEntry("Phone", Sentinel);

        Assert.DoesNotContain(Sentinel, entry.ToString());
    }

    [Fact]
    public void TrustedPublicInfoEntry_Interpolation_DoesNotContainSentinel()
    {
        var entry = new TrustedPublicInfoEntry("Phone", Sentinel);

        Assert.DoesNotContain(Sentinel, $"{entry}");
    }

    [Fact]
    public void TrustedPublicInfoEntry_ToString_ContainsOnlyPiiTypeId()
    {
        var entry = new TrustedPublicInfoEntry("Phone", Sentinel);

        string rendered = entry.ToString();

        Assert.Contains(nameof(TrustedPublicInfoEntry.PiiTypeId), rendered);
        Assert.Contains("Phone", rendered);
        // "Value =" is the field-assignment pattern this type's own format would use to expose
        // the sensitive field -- same discipline as the Windows/Detection precedents.
        Assert.DoesNotContain("Value =", rendered);
    }

    // ==================================================================
    // 3/4. ExceptionEntry
    // ==================================================================

    [Fact]
    public void ExceptionEntry_ToString_DoesNotContainSentinel()
    {
        var entry = new ExceptionEntry("Email", Sentinel);

        Assert.DoesNotContain(Sentinel, entry.ToString());
    }

    [Fact]
    public void ExceptionEntry_Interpolation_DoesNotContainSentinel()
    {
        var entry = new ExceptionEntry("Email", Sentinel);

        Assert.DoesNotContain(Sentinel, $"{entry}");
    }

    [Fact]
    public void ExceptionEntry_ToString_ContainsOnlyPiiTypeId()
    {
        var entry = new ExceptionEntry("Email", Sentinel);

        string rendered = entry.ToString();

        Assert.Contains(nameof(ExceptionEntry.PiiTypeId), rendered);
        Assert.Contains("Email", rendered);
        Assert.DoesNotContain("Value =", rendered);
    }

    // ==================================================================
    // Debugger surface
    // ==================================================================

    [Theory]
    [InlineData(typeof(TrustedPublicInfoEntry))]
    [InlineData(typeof(ExceptionEntry))]
    public void StorageDtos_HaveNoDebuggerDisplayOrTypeProxyAttributes(Type type)
    {
        var attributes = type.GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }

    // ==================================================================
    // Serialization compatibility -- ToString hardening must have zero effect on the JSON shape
    // System.Text.Json produces, since PrivonLocalStore reflects over properties, never ToString.
    // ==================================================================

    [Fact]
    public void TrustedPublicInfoEntry_JsonShape_StillOnlyPiiTypeIdAndValue()
    {
        var json = JsonSerializer.Serialize(new TrustedPublicInfoEntry("Phone", Sentinel));

        using var doc = JsonDocument.Parse(json);
        var propertyNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(new[] { "PiiTypeId", "Value" }, propertyNames);
        Assert.Equal(Sentinel, doc.RootElement.GetProperty("Value").GetString());
    }

    [Fact]
    public void ExceptionEntry_JsonShape_StillOnlyPiiTypeIdAndValue()
    {
        var json = JsonSerializer.Serialize(new ExceptionEntry("Email", Sentinel));

        using var doc = JsonDocument.Parse(json);
        var propertyNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(new[] { "PiiTypeId", "Value" }, propertyNames);
        Assert.Equal(Sentinel, doc.RootElement.GetProperty("Value").GetString());
    }

    // ==================================================================
    // Real Storage round-trip -- proves ToString hardening has zero effect on the actual
    // save -> encrypted v2 payload -> load path (real PrivonLocalStore, temp directory, same
    // pattern as TypedSchemaV2Tests).
    // ==================================================================

    [Fact]
    public void TrustedPublicInfo_RealStorageRoundTrip_StillPreservesPiiTypeIdAndValueExactly()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var entries = new List<TrustedPublicInfoEntry> { new("Phone", Sentinel) };

        store.SaveTrustedPublicInfo(entries);
        var loaded = store.LoadTrustedPublicInfo();

        Assert.Equal(entries, loaded);
        Assert.Equal("Phone", loaded[0].PiiTypeId);
        Assert.Equal(Sentinel, loaded[0].Value);
    }

    [Fact]
    public void Exceptions_RealStorageRoundTrip_StillPreservesPiiTypeIdAndValueExactly()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var entries = new List<ExceptionEntry> { new("Email", Sentinel) };

        store.SaveExceptions(entries);
        var loaded = store.LoadExceptions();

        Assert.Equal(entries, loaded);
        Assert.Equal("Email", loaded[0].PiiTypeId);
        Assert.Equal(Sentinel, loaded[0].Value);
    }
}
