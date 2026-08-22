using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Phase 3A.4 STEP3.2 -- Clipboard Raw-Text Diagnostic Surface Hardening regression.
// RAW_CLIPBOARD_DIAGNOSTIC_SURFACE: no public clipboard result/snapshot type's ToString() (nor
// any recursive record formatting reachable through one) may ever expose raw clipboard text --
// only metadata (Outcome/SequenceNumber/HasReliableSequence/Win32Error/ClipboardMutated/
// ResultSequence). Synthetic sentinel only; no real clipboard content is ever involved.
public class ClipboardDiagnosticSurfaceTests
{
    private const string Sentinel = "RAW-PII-SENTINEL-739184";

    // ---- 1/2. ClipboardTextSnapshot.ToString() never contains the sentinel or the raw Text
    // value at all ----
    [Fact]
    public void ClipboardTextSnapshot_ToString_DoesNotContainSentinel()
    {
        var snapshot = new ClipboardTextSnapshot(SequenceNumber: 42, HasReliableSequence: true, Text: Sentinel);

        string rendered = snapshot.ToString();

        Assert.DoesNotContain(Sentinel, rendered);
    }

    [Fact]
    public void ClipboardTextSnapshot_ToString_DoesNotContainRawTextValue()
    {
        var text = "홍길동 010-0000-0000 test@example.com";
        var snapshot = new ClipboardTextSnapshot(SequenceNumber: 1, HasReliableSequence: true, Text: text);

        string rendered = snapshot.ToString();

        Assert.DoesNotContain(text, rendered);
    }

    [Fact]
    public void ClipboardTextSnapshot_ToString_ContainsOnlyPermittedMetadata()
    {
        var snapshot = new ClipboardTextSnapshot(SequenceNumber: 42, HasReliableSequence: true, Text: Sentinel);

        string rendered = snapshot.ToString();

        Assert.Contains("42", rendered);
        Assert.Contains("True", rendered);
        Assert.Contains(nameof(ClipboardTextSnapshot.SequenceNumber), rendered);
        Assert.Contains(nameof(ClipboardTextSnapshot.HasReliableSequence), rendered);
        // "Text" alone would also match inside the type name "ClipboardTextSnapshot" itself --
        // assert against the field-assignment pattern our own ToString format actually uses, not
        // the bare substring.
        Assert.DoesNotContain("Text =", rendered);
    }

    // ---- 3/4. ClipboardTextReadResult.Success(...).ToString() never contains the sentinel,
    // and contains only permitted metadata ----
    [Fact]
    public void ClipboardTextReadResult_SuccessToString_DoesNotContainSentinel()
    {
        var snapshot = new ClipboardTextSnapshot(SequenceNumber: 7, HasReliableSequence: true, Text: Sentinel);
        var result = ClipboardTextReadResult.Success(snapshot);

        string rendered = result.ToString();

        Assert.DoesNotContain(Sentinel, rendered);
    }

    [Fact]
    public void ClipboardTextReadResult_SuccessToString_ContainsOnlyPermittedMetadata()
    {
        var snapshot = new ClipboardTextSnapshot(SequenceNumber: 7, HasReliableSequence: true, Text: Sentinel);
        var result = ClipboardTextReadResult.Success(snapshot);

        string rendered = result.ToString();

        Assert.Contains(nameof(ClipboardTextReadResult.Outcome), rendered);
        Assert.Contains(ClipboardReadOutcome.Success.ToString(), rendered);
        Assert.Contains("SequenceNumber", rendered);
        Assert.Contains("HasReliableSequence", rendered);
        Assert.DoesNotContain("Text =", rendered);
    }

    // ---- 5. Failure result ToString contains Outcome/Win32Error metadata only ----
    [Fact]
    public void ClipboardTextReadResult_FailureToString_ContainsOnlyOutcomeAndWin32Error()
    {
        var result = ClipboardTextReadResult.Failure(ClipboardReadOutcome.NativeFailure, win32Error: 6);

        string rendered = result.ToString();

        Assert.Contains(nameof(ClipboardTextReadResult.Outcome), rendered);
        Assert.Contains(ClipboardReadOutcome.NativeFailure.ToString(), rendered);
        Assert.Contains(nameof(ClipboardTextReadResult.Win32Error), rendered);
        Assert.Contains("6", rendered);
        Assert.DoesNotContain("Text =", rendered);
        Assert.DoesNotContain("SequenceNumber", rendered);
    }

    [Fact]
    public void ClipboardTextReadResult_FailureToString_WithNullWin32Error_DoesNotThrow()
    {
        var result = ClipboardTextReadResult.Failure(ClipboardReadOutcome.FormatUnavailable);

        string rendered = result.ToString();

        Assert.Contains("FormatUnavailable", rendered);
        Assert.Contains("null", rendered);
    }

    // ---- 6. No public clipboard diagnostic/result object's ToString() exposes the sentinel,
    // including via recursive record formatting -- ClipboardWriteResult carries no text-bearing
    // field at all (structural, not a literal sentinel embed), confirmed via reflection so this
    // stays true even if a future field is added carelessly. ----
    [Fact]
    public void ClipboardWriteResult_HasNoStringTypedField()
    {
        var properties = typeof(ClipboardWriteResult).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(properties, p => p.PropertyType == typeof(string));
    }

    [Fact]
    public void ClipboardWriteResult_ToString_DoesNotThrow_AndIsMetadataOnly()
    {
        var success = ClipboardWriteResult.Success(resultSequence: 5);
        var failure = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true, win32Error: 6, resultSequence: 5);

        string successRendered = success.ToString();
        string failureRendered = failure.ToString();

        Assert.Contains(nameof(ClipboardWriteOutcome.Success), successRendered);
        Assert.Contains(nameof(ClipboardWriteOutcome.ReadBackMismatch), failureRendered);
    }

    // ---- structural: neither ClipboardTextSnapshot nor ClipboardTextReadResult nor
    // ClipboardWriteResult declares DebuggerDisplay/DebuggerTypeProxy -- their ToString overrides
    // (or, for ClipboardWriteResult, the absence of any text-bearing field) are the only
    // diagnostic surface. ----
    [Theory]
    [InlineData(typeof(ClipboardTextSnapshot))]
    [InlineData(typeof(ClipboardTextReadResult))]
    [InlineData(typeof(ClipboardWriteResult))]
    public void ClipboardResultTypes_HaveNoDebuggerDisplayOrTypeProxyAttributes(Type type)
    {
        var attributes = type.GetCustomAttributes(inherit: false).Select(a => a.GetType().Name);

        Assert.DoesNotContain(attributes, name => name.Contains("DebuggerDisplay") || name.Contains("DebuggerTypeProxy"));
    }
}
