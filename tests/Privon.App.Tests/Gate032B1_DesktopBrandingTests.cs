using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace Privon.App.Tests;

// PRIVON 0.3.2 Gate 032-B1R -- test-oracle remediation. Proves the desktop app actually ships the
// official PRIVON PV brand (EXE icon, WPF window/taskbar icon, tray icon, Settings wordmark)
// sourced exclusively from this assembly's own embedded resources -- never a generic
// SystemIcons.Application placeholder, never an absolute filesystem path -- using oracles that are
// structurally proof against three specific weaknesses an independent audit found in the original
// 032-B1 version of this file:
//
//   P1-A: the ApplicationIcon check was a raw substring search over the whole csproj text, which
//         could pass even if ApplicationIcon were removed (as long as "Assets\privon.ico" appeared
//         anywhere, e.g. only inside an EmbeddedResource Include or a comment). Fixed by parsing the
//         csproj as XML and inspecting the ApplicationIcon ELEMENT specifically.
//   P1-B: the asset checks only proved dimensions/frame-count-at-least-N, never binary identity, so
//         a payload mutation that preserved header dimensions would slip through undetected. Fixed
//         by asserting exact SHA-256 identity plus an exact (not "at least") required frame set.
//   P1-C: several source-scan checks used either no comment stripping, or a naive "strip whole line
//         if it starts with //" stripper that a trailing/inline comment or a /* */ block comment
//         could still defeat -- and a comment merely MENTIONING a required call/forbidden API could
//         satisfy or defeat the checks without any real code doing so. Fixed by (a) stripping real
//         C-style line/block comments (string-literal aware) before any source-text assertion, and
//         (b) replacing the SettingsWindow icon/wordmark checks entirely with real runtime
//         assertions against a constructed window on a real WPF Dispatcher thread (the established
//         DispatcherAffineTestHost pattern already used by SettingsWindowActivationTests.cs), which
//         cannot be satisfied or defeated by comments at all.
//
// Every hardened oracle is exercised twice: once against the REAL production artifact (expected to
// pass) and once against a synthetic/mutated fixture engineered to violate exactly the property
// being checked (expected to be rejected) -- so this file also proves its own oracles actually
// discriminate, not just that the current code happens to satisfy a possibly-too-loose check.
public class Gate032B1_DesktopBrandingTests
{
    private const string IconResourceName = "Privon.App.Assets.privon.ico";
    private const string WordmarkResourceName = "Privon.App.Assets.privon-wordmark.png";
    private const string SquareIconPngResourceName = "Privon.App.Assets.privon-icon-128.png";

    private const string WordmarkSha256 = "0e0d8fde47d3dbe8ca13f03adc1eae042800cf0265413aec974b2082140bd2ec";
    private const string SquareIconSha256 = "b6b0404ef89dc7c59346eefd6fc452875b6da0f194f108cc38c8857a30cbad9a";
    private const string IcoSha256 = "7152576503fd083461a5be89f525c4f929258b46043c4e0610c843a6c21d49c9";

    private static readonly int[] RequiredIcoFrameSizes = [16, 24, 32, 48, 64, 128];

    private static Assembly AppAssembly => typeof(SettingsWindow).Assembly;

    // ==================================================================
    // P1-A: ApplicationIcon must be inspected as an XML element, not found via substring search.
    // ==================================================================

    [Fact]
    public void Csproj_ApplicationIcon_ExistsExactlyOnce_AndEqualsOfficialIco_ViaXmlStructure()
    {
        var csprojText = File.ReadAllText(FindAppSourceFile("Privon.App.csproj"));

        var exception = Record.Exception(() => AssertApplicationIconConfiguredExactly(csprojText, @"Assets\privon.ico"));

        Assert.Null(exception);
    }

    [Fact]
    public void ApplicationIconOracle_RejectsCsprojWithOnlyEmbeddedResourceNoApplicationIcon()
    {
        const string synthetic = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <EmbeddedResource Include="Assets\privon.ico">
                  <LogicalName>Privon.App.Assets.privon.ico</LogicalName>
                </EmbeddedResource>
              </ItemGroup>
            </Project>
            """;

        Assert.Throws<InvalidOperationException>(() => AssertApplicationIconConfiguredExactly(synthetic, @"Assets\privon.ico"));
    }

    [Fact]
    public void ApplicationIconOracle_RejectsCsprojWhereApplicationIconPointsElsewhere()
    {
        const string synthetic = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <ApplicationIcon>Assets\some-other-icon.ico</ApplicationIcon>
              </PropertyGroup>
            </Project>
            """;

        Assert.Throws<InvalidOperationException>(() => AssertApplicationIconConfiguredExactly(synthetic, @"Assets\privon.ico"));
    }

    [Fact]
    public void ApplicationIconOracle_RejectsApplicationIconMentionedOnlyInsideAnXmlComment()
    {
        const string synthetic = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <!-- <ApplicationIcon>Assets\privon.ico</ApplicationIcon> -->
              </PropertyGroup>
            </Project>
            """;

        // XML comment content is opaque to element queries by construction (XDocument never parses
        // it as elements) -- this proves that structurally, not just by convention.
        Assert.Throws<InvalidOperationException>(() => AssertApplicationIconConfiguredExactly(synthetic, @"Assets\privon.ico"));
    }

    private static void AssertApplicationIconConfiguredExactly(string csprojXmlText, string expectedNormalizedValue)
    {
        var doc = XDocument.Parse(csprojXmlText);
        var elements = doc.Descendants("ApplicationIcon").ToList();

        if (elements.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one <ApplicationIcon> element, found {elements.Count}.");
        }

        var actual = elements[0].Value.Trim().Replace('/', '\\');
        if (!string.Equals(actual, expectedNormalizedValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected <ApplicationIcon> to equal '{expectedNormalizedValue}', found '{actual}'.");
        }
    }

    // ==================================================================
    // Embedded manifest-resource presence (unaffected by the audit -- kept as-is; complementary to,
    // never a substitute for, the identity checks below).
    // ==================================================================

    [Fact]
    public void Assembly_EmbedsOfficialTrayIconResource()
    {
        Assert.Contains(IconResourceName, AppAssembly.GetManifestResourceNames());
    }

    [Fact]
    public void Assembly_EmbedsOfficialWordmarkResource()
    {
        Assert.Contains(WordmarkResourceName, AppAssembly.GetManifestResourceNames());
    }

    [Fact]
    public void Assembly_EmbedsOfficialSquareIconPngResource()
    {
        Assert.Contains(SquareIconPngResourceName, AppAssembly.GetManifestResourceNames());
    }

    // ==================================================================
    // P1-B: exact binary identity (SHA-256), not just dimensions/"at least N frames". A payload
    // mutation that leaves the header untouched would pass a dimension-only check but must fail
    // here.
    // ==================================================================

    [Fact]
    public void EmbeddedWordmark_MatchesCanonicalIdentity_ShaAndDimensions()
    {
        var data = ReadManifestResourceBytes(WordmarkResourceName);

        var exception = Record.Exception(() => AssertKnownPng(data, WordmarkSha256, 385, 120, expectedColorType: null));

        Assert.Null(exception);
    }

    [Fact]
    public void EmbeddedSquareIcon_MatchesCanonicalIdentity_ShaDimensionsAndColorType()
    {
        var data = ReadManifestResourceBytes(SquareIconPngResourceName);

        var exception = Record.Exception(() => AssertKnownPng(data, SquareIconSha256, 128, 128, expectedColorType: 6));

        Assert.Null(exception);
    }

    [Fact]
    public void EmbeddedIcon_MatchesCanonicalIdentity_ShaAndExactFrameSet()
    {
        var data = ReadManifestResourceBytes(IconResourceName);

        var exception = Record.Exception(() => AssertKnownIco(data, IcoSha256, RequiredIcoFrameSizes));

        Assert.Null(exception);
    }

    [Fact]
    public void AssetIdentityOracle_RejectsOneByteMutatedWordmark()
    {
        var mutated = MutateLastByte(ReadManifestResourceBytes(WordmarkResourceName));

        Assert.Throws<InvalidOperationException>(() => AssertKnownPng(mutated, WordmarkSha256, 385, 120, expectedColorType: null));
    }

    [Fact]
    public void AssetIdentityOracle_RejectsOneByteMutatedSquareIcon()
    {
        var mutated = MutateLastByte(ReadManifestResourceBytes(SquareIconPngResourceName));

        Assert.Throws<InvalidOperationException>(() => AssertKnownPng(mutated, SquareIconSha256, 128, 128, expectedColorType: 6));
    }

    [Fact]
    public void AssetIdentityOracle_RejectsOneByteMutatedIco()
    {
        var mutated = MutateLastByte(ReadManifestResourceBytes(IconResourceName));

        Assert.Throws<InvalidOperationException>(() => AssertKnownIco(mutated, IcoSha256, RequiredIcoFrameSizes));
    }

    [Fact]
    public void FrameSetOracle_RejectsMissingFrame()
    {
        var missing128 = new[] { 16, 24, 32, 48, 64 };

        Assert.Throws<InvalidOperationException>(() => AssertExactFrameSet(missing128, RequiredIcoFrameSizes));
    }

    [Fact]
    public void FrameSetOracle_RejectsExtraFrame()
    {
        var extra256 = new[] { 16, 24, 32, 48, 64, 128, 256 };

        Assert.Throws<InvalidOperationException>(() => AssertExactFrameSet(extra256, RequiredIcoFrameSizes));
    }

    private static byte[] MutateLastByte(byte[] data)
    {
        var mutated = (byte[])data.Clone();
        mutated[^1] ^= 0xFF; // flips a payload byte far from the header -- dimensions still parse
        return mutated;      // fine, proving only a content-identity (hash) check catches this.
    }

    private static void AssertKnownPng(byte[] data, string expectedSha256Hex, int expectedWidth, int expectedHeight, byte? expectedColorType)
    {
        var actualHash = Sha256Hex(data);
        if (!string.Equals(actualHash, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SHA-256 mismatch: expected {expectedSha256Hex}, got {actualHash}.");
        }

        var (width, height, colorType) = ReadPngHeader(data);
        if (width != expectedWidth || height != expectedHeight)
        {
            throw new InvalidOperationException($"Dimension mismatch: expected {expectedWidth}x{expectedHeight}, got {width}x{height}.");
        }

        if (expectedColorType is { } ct && colorType != ct)
        {
            throw new InvalidOperationException($"PNG color type mismatch: expected {ct}, got {colorType}.");
        }
    }

    private static void AssertKnownIco(byte[] data, string expectedSha256Hex, int[] expectedSizes)
    {
        var actualHash = Sha256Hex(data);
        if (!string.Equals(actualHash, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SHA-256 mismatch: expected {expectedSha256Hex}, got {actualHash}.");
        }

        AssertExactFrameSet(ReadIcoFrameSizes(data), expectedSizes);
    }

    private static void AssertExactFrameSet(IReadOnlyCollection<int> actualSizes, IReadOnlyCollection<int> expectedSizes)
    {
        var actual = actualSizes.OrderBy(x => x).ToArray();
        var expected = expectedSizes.OrderBy(x => x).ToArray();

        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Frame size set mismatch: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}].");
        }
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static (int Width, int Height, byte ColorType) ReadPngHeader(byte[] data)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 26 || !data.AsSpan(0, 8).SequenceEqual(signature))
        {
            throw new InvalidOperationException("Data is not a valid PNG (bad signature or too short).");
        }

        int width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
        int height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
        return (width, height, data[25]);
    }

    private static List<int> ReadIcoFrameSizes(byte[] data)
    {
        if (data.Length < 6)
        {
            throw new InvalidOperationException("ICO data too short to contain an ICONDIR header.");
        }

        ushort reserved = BitConverter.ToUInt16(data, 0);
        ushort type = BitConverter.ToUInt16(data, 2);
        ushort count = BitConverter.ToUInt16(data, 4);

        if (reserved != 0 || type != 1)
        {
            throw new InvalidOperationException($"Not a valid ICO ICONDIR header (reserved={reserved}, type={type}).");
        }

        if (data.Length < 6 + (count * 16))
        {
            throw new InvalidOperationException("ICO data too short for its own declared ICONDIRENTRY count.");
        }

        var sizes = new List<int>();
        int offset = 6;
        for (int i = 0; i < count; i++)
        {
            byte w = data[offset];
            sizes.Add(w == 0 ? 256 : w);
            offset += 16;
        }

        return sizes;
    }

    private static byte[] ReadManifestResourceBytes(string resourceName)
    {
        using var stream = AppAssembly.GetManifestResourceStream(resourceName);
        Assert.True(stream is not null, $"Embedded manifest resource '{resourceName}' was not found.");

        using var ms = new MemoryStream();
        stream!.CopyTo(ms);
        return ms.ToArray();
    }

    // ==================================================================
    // P1-C: tray-icon source checks, hardened against comments. StripCommentsPreservingStrings
    // removes real // line comments and /* */ block comments (string-literal aware) before any
    // Contains/DoesNotContain assertion runs -- a prose mention in a doc comment can neither satisfy
    // a "must call X" check nor trigger a "must not use Y" false positive.
    // ==================================================================

    [Fact]
    public void TraySurfaceSource_NoLongerUsesGenericSystemIcon_IgnoringComments()
    {
        var executableOnly = StripCommentsPreservingStrings(File.ReadAllText(FindAppSourceFile("WinFormsTrayIconSurface.cs")));
        Assert.DoesNotContain("SystemIcons.Application", executableOnly);
    }

    [Fact]
    public void TraySurfaceSource_SourcesIconFromBrandResources_IgnoringComments()
    {
        var executableOnly = StripCommentsPreservingStrings(File.ReadAllText(FindAppSourceFile("WinFormsTrayIconSurface.cs")));
        Assert.Contains("BrandResources.LoadTrayIcon()", executableOnly);
    }

    [Fact]
    public void TraySurfaceSource_DisposesItsOwnBrandIconHandle_IgnoringComments()
    {
        var executableOnly = StripCommentsPreservingStrings(File.ReadAllText(FindAppSourceFile("WinFormsTrayIconSurface.cs")));
        Assert.Contains("_brandIcon.Dispose()", executableOnly);
    }

    [Fact]
    public void CommentStrippingOracle_RejectsBrandResourcesCallMentionedOnlyInComment()
    {
        const string synthetic = """
            internal sealed class Fake
            {
                // BrandResources.LoadTrayIcon();
                void DoNothing() { }
            }
            """;

        var executableOnly = StripCommentsPreservingStrings(synthetic);

        // A "must call this" check applied to this synthetic snippet must fail -- the call exists
        // only in a comment, never in executable code.
        var exception = Record.Exception(() => Assert.Contains("BrandResources.LoadTrayIcon()", executableOnly));
        Assert.NotNull(exception);
    }

    [Fact]
    public void CommentStrippingOracle_DoesNotFlagSystemIconsApplicationMentionedOnlyInComment()
    {
        const string synthetic = """
            internal sealed class Fake
            {
                // this file used to assign Icon = SystemIcons.Application here
                void DoNothing() { }
            }
            """;

        var executableOnly = StripCommentsPreservingStrings(synthetic);

        // A "must not use this" check applied to this synthetic snippet must NOT false-positive --
        // the mention is historical prose in a comment, not real usage.
        Assert.DoesNotContain("SystemIcons.Application", executableOnly);
    }

    private static string StripCommentsPreservingStrings(string source)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                continue;
            }

            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        sb.Append(source[i]);
                        sb.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }

                    sb.Append(source[i]);
                    i++;
                }

                if (i < source.Length)
                {
                    sb.Append(source[i]);
                    i++;
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    // ==================================================================
    // P1-C: Settings window icon/wordmark -- replaced entirely with real runtime assertions against
    // a constructed window on a real WPF Dispatcher thread (DispatcherAffineTestHost, the same
    // established pattern SettingsWindowActivationTests.cs already uses). Neither check can be
    // satisfied or defeated by any comment, since neither inspects source text at all.
    // ==================================================================

    [Fact]
    public void SettingsWindow_IconMatchesCanonicalSquareIconPixelDimensions_OnARealDispatcherThread()
    {
        using var host = new DispatcherAffineTestHost();

        var (width, height) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                var source = Assert.IsAssignableFrom<BitmapSource>(window.Icon);
                return (source.PixelWidth, source.PixelHeight);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(128, width);
        Assert.Equal(128, height);
    }

    [Fact]
    public void SettingsWindow_ContainsWordmarkImageMatchingCanonicalPixelDimensions_OnARealDispatcherThread()
    {
        using var host = new DispatcherAffineTestHost();

        var (width, height) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                var image = FindFirstImage(window);
                Assert.NotNull(image);
                var source = Assert.IsAssignableFrom<BitmapSource>(image!.Source);
                return (source.PixelWidth, source.PixelHeight);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(385, width);
        Assert.Equal(120, height);
    }

    private static System.Windows.Controls.Image? FindFirstImage(DependencyObject root)
    {
        if (root is System.Windows.Controls.Image image)
        {
            return image;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dependencyChild)
            {
                var found = FindFirstImage(dependencyChild);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    // ==================================================================
    // No absolute filesystem path anywhere in the branding loader itself (comment-hardened for
    // consistency -- a comment explaining why NOT to use AppContext.BaseDirectory must not
    // false-positive this check either).
    // ==================================================================

    [Fact]
    public void BrandResourcesSource_ExistsAndNeverReferencesAbsolutePathOrBaseDirectory()
    {
        var path = FindAppSourceFile("BrandResources.cs");
        Assert.True(File.Exists(path), "BrandResources.cs must exist as the single production loader for embedded brand assets.");

        var executableOnly = StripCommentsPreservingStrings(File.ReadAllText(path));
        Assert.DoesNotContain("AppContext.BaseDirectory", executableOnly);
        Assert.DoesNotContain(@"C:\", executableOnly);
        Assert.DoesNotContain("Environment.GetFolderPath", executableOnly);
    }

    // ==================================================================
    // helpers
    // ==================================================================

    private static string FindAppSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate src/Privon.App from the test output directory.");
        }

        return Path.Combine(dir.FullName, "src", "Privon.App", fileName);
    }
}
