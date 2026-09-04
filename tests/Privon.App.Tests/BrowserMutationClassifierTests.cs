using AppBrowser = Privon.App.NativeMessagingBrowser;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5G.1F audit remediation -- validates the BrowserMutationClassifier test oracle
// itself, in complete isolation from any real coordinator/harness. Every entry below is a synthetic,
// log-format-realistic string matching FakeNativeMessagingHostRegistrationEnvironment's own exact
// $"{nameof(Member)}({args})" logging shape -- never a real CallLog produced by exercising
// production code, since this file exists to prove the CLASSIFIER is correct, not to prove anything
// about production behavior.
public class BrowserMutationClassifierTests
{
    private static readonly string ChromeManifestPath = NativeMessagingHostRegistrationLayout.ExpectedManifestPath(AppBrowser.Chrome);
    private static readonly string EdgeManifestPath = NativeMessagingHostRegistrationLayout.ExpectedManifestPath(AppBrowser.Edge);

    // ==================================================================
    // Demonstrates WHY the previous case-sensitive "Chrome"/"Edge" substring filter was insufficient
    // -- the exact same log entries the new classifier correctly detects below are NOT matched by a
    // literal capitalized substring search, because the manifest path itself is lowercase
    // ("chrome-host.json"/"edge-host.json") and contains neither "Chrome" nor "Edge" as a substring.
    // ==================================================================

    [Fact]
    public void OldSubstringPredicate_NeverMatchesChromeManifestWrite_ProvingItWasUnsound()
    {
        string entry = $"WriteManifest({ChromeManifestPath})";

        Assert.Contains("chrome-host.json", ChromeManifestPath, StringComparison.Ordinal); // sanity: path really is lowercase
        Assert.DoesNotContain("Chrome", entry, StringComparison.Ordinal); // the old predicate's exact search term never appears
        Assert.True(BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Chrome)); // the new classifier still catches it
    }

    [Fact]
    public void OldSubstringPredicate_NeverMatchesEdgeManifestDelete_ProvingItWasUnsound()
    {
        string entry = $"DeleteManifest({EdgeManifestPath})";

        Assert.Contains("edge-host.json", EdgeManifestPath, StringComparison.Ordinal);
        Assert.DoesNotContain("Edge", entry, StringComparison.Ordinal);
        Assert.True(BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Edge));
    }

    // ==================================================================
    // Manifest mutation detection -- lowercase filenames, both operations, both browsers.
    // ==================================================================

    [Fact]
    public void WriteManifest_TargetingChromePath_IsClassifiedAsChromeMutation()
    {
        string entry = $"WriteManifest({ChromeManifestPath})";
        Assert.True(BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Chrome));
    }

    [Fact]
    public void DeleteManifest_TargetingChromePath_IsClassifiedAsChromeMutation()
    {
        string entry = $"DeleteManifest({ChromeManifestPath})";
        Assert.True(BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Chrome));
    }

    [Fact]
    public void WriteManifest_TargetingEdgePath_IsClassifiedAsEdgeMutation()
    {
        string entry = $"WriteManifest({EdgeManifestPath})";
        Assert.True(BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Edge));
    }

    [Fact]
    public void DeleteManifest_TargetingEdgePath_IsClassifiedAsEdgeMutation()
    {
        string entry = $"DeleteManifest({EdgeManifestPath})";
        Assert.True(BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Edge));
    }

    // ==================================================================
    // Cross-browser false positives absent.
    // ==================================================================

    [Fact]
    public void ChromeDetector_DoesNotClassifyAnEdgeManifestWrite_AsChromeMutation()
    {
        string entry = $"WriteManifest({EdgeManifestPath})";
        Assert.False(BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Chrome));
    }

    [Fact]
    public void ChromeDetector_DoesNotClassifyAnEdgeManifestDelete_AsChromeMutation()
    {
        string entry = $"DeleteManifest({EdgeManifestPath})";
        Assert.False(BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Chrome));
    }

    [Fact]
    public void EdgeDetector_DoesNotClassifyAChromeManifestWrite_AsEdgeMutation()
    {
        string entry = $"WriteManifest({ChromeManifestPath})";
        Assert.False(BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Edge));
    }

    [Fact]
    public void EdgeDetector_DoesNotClassifyAChromeManifestDelete_AsEdgeMutation()
    {
        string entry = $"DeleteManifest({ChromeManifestPath})";
        Assert.False(BrowserMutationClassifier.IsMutationFor(entry, AppBrowser.Edge));
    }

    [Fact]
    public void ChromeDetector_DoesNotClassifyAnEdgeRegistryWrite_AsChromeMutation()
    {
        Assert.False(BrowserMutationClassifier.IsMutationFor($"SetSubkeyDefaultValue({AppBrowser.Edge}, {EdgeManifestPath})", AppBrowser.Chrome));
        Assert.False(BrowserMutationClassifier.IsMutationFor($"DeleteSubkey({AppBrowser.Edge})", AppBrowser.Chrome));
    }

    [Fact]
    public void EdgeDetector_DoesNotClassifyAChromeRegistryWrite_AsEdgeMutation()
    {
        Assert.False(BrowserMutationClassifier.IsMutationFor($"SetSubkeyDefaultValue({AppBrowser.Chrome}, {ChromeManifestPath})", AppBrowser.Edge));
        Assert.False(BrowserMutationClassifier.IsMutationFor($"DeleteSubkey({AppBrowser.Chrome})", AppBrowser.Edge));
    }

    // ==================================================================
    // Registry mutation detection -- own-browser positive, sanity.
    // ==================================================================

    [Fact]
    public void SetSubkeyDefaultValue_TargetingChrome_IsClassifiedAsChromeMutation()
    {
        Assert.True(BrowserMutationClassifier.IsMutationFor($"SetSubkeyDefaultValue({AppBrowser.Chrome}, {ChromeManifestPath})", AppBrowser.Chrome));
    }

    [Fact]
    public void DeleteSubkey_TargetingEdge_IsClassifiedAsEdgeMutation()
    {
        Assert.True(BrowserMutationClassifier.IsMutationFor($"DeleteSubkey({AppBrowser.Edge})", AppBrowser.Edge));
    }

    // ==================================================================
    // Read-only operations are never classified as mutation, for either browser.
    // ==================================================================

    [Fact]
    public void ReadOnlyRegistryOperations_OnChrome_AreNeverClassifiedAsMutation()
    {
        Assert.False(BrowserMutationClassifier.IsMutationFor($"SubkeyExists({AppBrowser.Chrome})", AppBrowser.Chrome));
        Assert.False(BrowserMutationClassifier.IsMutationFor($"GetSubkeyDefaultValue({AppBrowser.Chrome})", AppBrowser.Chrome));
    }

    [Fact]
    public void ReadOnlyRegistryOperations_OnEdge_AreNeverClassifiedAsMutation()
    {
        Assert.False(BrowserMutationClassifier.IsMutationFor($"SubkeyExists({AppBrowser.Edge})", AppBrowser.Edge));
        Assert.False(BrowserMutationClassifier.IsMutationFor($"GetSubkeyDefaultValue({AppBrowser.Edge})", AppBrowser.Edge));
    }

    [Fact]
    public void ReadOnlyManifestOperations_OnChromePath_AreNeverClassifiedAsMutation()
    {
        Assert.False(BrowserMutationClassifier.IsMutationFor($"ManifestExists({ChromeManifestPath})", AppBrowser.Chrome));
        Assert.False(BrowserMutationClassifier.IsMutationFor($"ReadManifest({ChromeManifestPath})", AppBrowser.Chrome));
    }

    [Fact]
    public void ReadOnlyManifestOperations_OnEdgePath_AreNeverClassifiedAsMutation()
    {
        Assert.False(BrowserMutationClassifier.IsMutationFor($"ManifestExists({EdgeManifestPath})", AppBrowser.Edge));
        Assert.False(BrowserMutationClassifier.IsMutationFor($"ReadManifest({EdgeManifestPath})", AppBrowser.Edge));
    }

    // ==================================================================
    // Case-insensitive path comparison -- Windows paths are case-insensitive, so the classifier must
    // not depend on the exact casing NativeMessagingHostRegistrationLayout happens to produce today.
    // ==================================================================

    [Fact]
    public void ManifestMutationDetection_IsCaseInsensitiveOnThePath()
    {
        string upperCasedEntry = $"WriteManifest({ChromeManifestPath.ToUpperInvariant()})";
        Assert.True(BrowserMutationClassifier.IsMutationFor(upperCasedEntry, AppBrowser.Chrome));
    }
}
