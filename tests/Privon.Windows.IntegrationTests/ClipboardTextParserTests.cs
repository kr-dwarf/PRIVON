using System.Text;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Pure unit tests for ClipboardTextParser -- no P/Invoke, no fake native, no owner thread.
// Directly exercises the bounded/NUL-terminated parsing rules against synthetic byte arrays.
public class ClipboardTextParserTests
{
    private static byte[] Utf16WithNul(string text) => Encoding.Unicode.GetBytes(text + '\0');

    [Fact]
    public void Parse_SimpleAsciiWithNul_ReturnsTextBeforeNul()
    {
        Assert.Equal("ABC", ClipboardTextParser.Parse(Utf16WithNul("ABC")));
    }

    [Fact]
    public void Parse_KoreanTextWithNul_ReturnsFullText()
    {
        Assert.Equal("안녕하세요", ClipboardTextParser.Parse(Utf16WithNul("안녕하세요")));
    }

    [Fact]
    public void Parse_MultilineTextWithNul_PreservesNewlines()
    {
        Assert.Equal("line1\r\nline2\nline3", ClipboardTextParser.Parse(Utf16WithNul("line1\r\nline2\nline3")));
    }

    [Fact]
    public void Parse_EmojiSurrogatePairWithNul_PreservesPair()
    {
        Assert.Equal("hi \U0001F600!", ClipboardTextParser.Parse(Utf16WithNul("hi \U0001F600!")));
    }

    [Fact]
    public void Parse_ZeroWidthCharacterWithNul_IsPreserved()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var text = "a" + zeroWidthSpace + "b";
        Assert.Equal(text, ClipboardTextParser.Parse(Utf16WithNul(text)));
    }

    // ---- 18. First NUL terminates -- trailing allocation content is never exposed ----
    [Fact]
    public void Parse_TrailingBytesAfterNul_AreNeverIncluded()
    {
        // "AB" + NUL + "Z" (leftover/uninitialized allocation content past the terminator).
        byte[] buffer = [0x41, 0x00, 0x42, 0x00, 0x00, 0x00, 0x5A, 0x00];
        Assert.Equal("AB", ClipboardTextParser.Parse(buffer));
    }

    // ---- 19. No NUL anywhere within the bound -> malformed, fail-closed ----
    [Fact]
    public void Parse_NoNulWithinBounds_ReturnsNull()
    {
        byte[] buffer = [0x41, 0x00, 0x42, 0x00]; // "AB", no terminator
        Assert.Null(ClipboardTextParser.Parse(buffer));
    }

    // ---- 16. Odd byte length -> not a whole number of UTF-16 code units -> malformed ----
    [Fact]
    public void Parse_OddByteLength_ReturnsNull()
    {
        byte[] buffer = [0x41, 0x00, 0x42]; // 3 bytes
        Assert.Null(ClipboardTextParser.Parse(buffer));
    }

    [Fact]
    public void Parse_EmptyBuffer_ReturnsNull()
    {
        Assert.Null(ClipboardTextParser.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Parse_NulAtStart_ReturnsEmptyString()
    {
        byte[] buffer = [0x00, 0x00, 0x41, 0x00]; // NUL immediately, "A" would be trailing garbage
        Assert.Equal(string.Empty, ClipboardTextParser.Parse(buffer));
    }
}
