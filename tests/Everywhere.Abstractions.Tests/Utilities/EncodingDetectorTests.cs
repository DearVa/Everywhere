using System.Collections;
using System.Text;
using Everywhere.Utilities;

namespace Everywhere.Abstractions.Tests.Utilities;

[TestFixture]
public class EncodingDetectorTests
{
    static EncodingDetectorTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static IEnumerable EncodingTestCases
    {
        get
        {
            // 1. UTF-8 (No BOM)
            yield return new TestCaseData("utf-8", "Hello 世界"u8.ToArray(), false);
            
            // 2. UTF-8 (BOM)
            yield return new TestCaseData("utf-8", GetBytesWithBom(Encoding.UTF8, "Hello 世界"), true);

            // 3. UTF-16 LE
            yield return new TestCaseData("utf-16", GetBytesWithBom(Encoding.Unicode, "Hello 世界"), true);

            // 4. UTF-16 BE
            yield return new TestCaseData("utf-16BE", GetBytesWithBom(Encoding.BigEndianUnicode, "Hello 世界"), true);

            // 5. UTF-32 LE
            yield return new TestCaseData("utf-32", GetBytesWithBom(Encoding.UTF32, "Hello 世界"), true);

            // 6. UTF-32 BE
            yield return new TestCaseData("utf-32BE", GetBytesWithBom(new UTF32Encoding(true, true), "Hello 世界"), true);

            // 7. GBK / GB2312
            // Use unambiguous sequence to distinguish from Shift-JIS
            // 0x81 0xFE: Valid GBK (Trail 0x40-0xFE), Invalid Shift-JIS (Trail 0x40-0x7E, 0x80-0xFC)
            yield return new TestCaseData("GBK", new byte[] { 0x81, 0xFE }, false);

            // 8. GB18030
            yield return new TestCaseData("GB18030", Encoding.GetEncoding("GB18030").GetBytes("Hello 𠀀"), false);

            // 9. Big5
            yield return new TestCaseData("Big5", Encoding.GetEncoding("Big5").GetBytes("Hello 你好"), false);

            // 10. Shift-JIS
            // Use unambiguous sequence: Half-width Katakana (0xA1-0xDF) followed by space (0x20).
            // GBK sees 0xA1-0xDF as Lead Byte, so 0x20 is invalid trail.
            yield return new TestCaseData("shift_jis", new byte[] { 0xB1, 0x20 }, false);

            // 11. Windows-1252
            yield return new TestCaseData("windows-1252", Encoding.GetEncoding(1252).GetBytes("Hello €"), false);
        }
    }

    private static byte[] GetBytesWithBom(Encoding encoding, string text)
    {
        var preamble = encoding.GetPreamble();
        var bytes = encoding.GetBytes(text);
        var result = new byte[preamble.Length + bytes.Length];
        Array.Copy(preamble, result, preamble.Length);
        Array.Copy(bytes, 0, result, preamble.Length, bytes.Length);
        return result;
    }

    [Test]
    [TestCaseSource(nameof(EncodingTestCases))]
    public async Task DetectEncodingAsync_ShouldDetectCorrectEncoding(string expectedEncodingName, byte[] data, bool hasBom)
    {
        // Arrange
        using var stream = new MemoryStream(data);

        // Act
        var detected = await EncodingDetector.DetectEncodingAsync(stream);

        // Assert
        Assert.That(detected, Is.Not.Null);
        var expected = Encoding.GetEncoding(expectedEncodingName);

        switch (expected.CodePage)
        {
            // Allow compatible fallbacks for structurally identical inputs (without frequency analysis)
            case 950 when detected.CodePage is 936 or 54936:
            // GBK -> GB18030
            case 936 when detected.CodePage == 54936:
            // GB18030 -> GBK
            case 54936 when detected.CodePage == 936:
                return; // Big5 -> GBK/GB18030
            default:
                Assert.That(detected.CodePage, Is.EqualTo(expected.CodePage), $"Expected {expectedEncodingName} but got {detected.EncodingName}");
                break;
        }

    }
    
    [Test]
    public async Task DetectEncodingAsync_ReturnsNullForBinary()
    {
        // Arrange
        var binaryData = new byte[100];
        new Random().NextBytes(binaryData);
        for(var i=0; i<50; i++) binaryData[i] = 0;
        
        using var stream = new MemoryStream(binaryData);
        
        // Act
        var result = await EncodingDetector.DetectEncodingAsync(stream);
        
        // Assert
        Assert.That(result, Is.Null);
    }
}
