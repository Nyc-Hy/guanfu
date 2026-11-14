using System.Text;

namespace Workbench.Documents;

/// <summary>
/// 纯文本编码探测。
///
/// 需求文档绝大多数是 UTF-8，但也有 GBK / GB18030 的老文件。
/// 直接按 UTF-8 解码会把中文变成一串 ""，agent 读到的全是乱码。
///
/// 回退顺序：BOM → 无 BOM 的 UTF-16（按 NUL 奇偶位判端序）→ 严格 UTF-8 → GB18030 → UTF-8 替换字符。
/// 严格 UTF-8 这一步很关键：能过就说明本来就是 UTF-8，不用去猜。
/// 但它必须排在 UTF-16 探测之后：NUL(0x00) 在 UTF-8 里是合法字节，
/// 无 BOM 的 UTF-16 文件能顺利通过严格 UTF-8 检查，解出来却是「a\0b\0c\0」这种夹着 NUL 的乱码。
/// </summary>
public static class TextEncodingDetector
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF32Encoding Utf32Le = new(bigEndian: false, byteOrderMark: false);
    private static readonly UTF32Encoding Utf32Be = new(bigEndian: true, byteOrderMark: false);
    private static readonly UnicodeEncoding StrictUtf16Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    static TextEncodingDetector()
    {
        // GB18030 的解码表不在 .NET 默认编码集里，要显式注册 CodePages 提供者
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>按字节内容解码文本，尽量不产生乱码。</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        // 1. 带 BOM 的文件编码是确定的
        if (TryDecodeWithBom(bytes, out var bomText))
        {
            return bomText;
        }

        // 2. 无 BOM 的 UTF-16：ASCII 字符的高位字节是 0x00，按奇偶位统计 NUL 就能判端序。
        //    必须抢在严格 UTF-8 之前，否则会被当成合法的 UTF-8。
        if (TryDecodeUtf16WithoutBom(bytes, denseNulOnly: true, out var utf16Text))
        {
            return utf16Text;
        }

        // 3. 严格 UTF-8：过了就是 UTF-8
        string? strictUtf8Text = null;
        try
        {
            strictUtf8Text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8，继续往下试
        }

        // 正文几乎全是中文时 NUL 很稀疏，比例判据会漏。但 UTF-8 文本里不该出现 U+0000，
        // 一旦解出 NUL（或压根不是合法 UTF-8），就放宽比例再试一次 UTF-16。
        if ((strictUtf8Text is null || strictUtf8Text.Contains('\0')) &&
            TryDecodeUtf16WithoutBom(bytes, denseNulOnly: false, out utf16Text))
        {
            return utf16Text;
        }

        if (strictUtf8Text is not null)
        {
            return strictUtf8Text;
        }

        // 4. GB18030：中文 Windows 上最常见的非 UTF-8 编码
        try
        {
            var gb18030 = Encoding.GetEncoding(
                "gb18030",
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            return gb18030.GetString(bytes);
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            // 连 GB18030 都不认，按 UTF-8 替换字符解码：宁可显示方块，也别抛异常
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 探测无 BOM 的 UTF-16。
    ///
    /// 判据：UTF-16 里 ASCII 字符是「低位字节 + 0x00」，所以 NUL 会规律地落在奇数位（小端）
    /// 或偶数位（大端），哪一侧多就是哪种端序。
    /// 注意不能要求另一侧为 0：中文码点也可能带 0 字节（如「需」U+9700 在小端下低位就是 0x00），
    /// 中英混排的文件两侧都会有 NUL，只能比多少。
    ///
    /// <paramref name="denseNulOnly"/> 为 true 时要求 NUL 至少占 1/8（英文为主的文档）；
    /// 为 false 时只要有一个 NUL 就试（中文为主的文档 NUL 很稀疏），靠解码结果再验一遍。
    /// </summary>
    private static bool TryDecodeUtf16WithoutBom(byte[] bytes, bool denseNulOnly, out string text)
    {
        text = string.Empty;

        // 少于两个码元或长度是奇数，都不可能是 UTF-16
        if (bytes.Length < 4 || bytes.Length % 2 != 0)
        {
            return false;
        }

        // 判端序只看开头一段就够，大文件不必全扫
        var sampleLength = Math.Min(bytes.Length, 4096) & ~1;
        var pairs = sampleLength / 2;
        var nulEven = 0;
        var nulOdd = 0;

        for (var i = 0; i < sampleLength; i += 2)
        {
            if (bytes[i] == 0)
            {
                nulEven++;
            }

            if (bytes[i + 1] == 0)
            {
                nulOdd++;
            }
        }

        // 一个 NUL 都没有，或者两侧一样多：没有端序证据，别猜
        if (nulEven == nulOdd)
        {
            return false;
        }

        var bigEndian = nulEven > nulOdd;
        if (denseNulOnly && Math.Max(nulEven, nulOdd) * 8 < pairs)
        {
            return false;
        }

        // 先按证据强的一侧解；解不通再试另一侧 ——
        // 正文几乎全是中文时 NUL 证据可能刚好指向反了（「需」这类码点会贡献反向 NUL）。
        if (TryDecodeUtf16(bytes, bigEndian, out text) ||
            TryDecodeUtf16(bytes, !bigEndian, out text))
        {
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>按指定端序严格解码，并检查结果像不像文本。</summary>
    private static bool TryDecodeUtf16(byte[] bytes, bool bigEndian, out string text)
    {
        text = string.Empty;

        string decoded;
        try
        {
            decoded = (bigEndian ? StrictUtf16Be : StrictUtf16Le).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 有落单的代理项，不是这个端序的 UTF-16
            return false;
        }

        // 解出来还带 NUL、替换字符或别的控制字符，说明端序/编码猜错了，交给后面的回退
        foreach (var c in decoded)
        {
            if (c is '\0' or '\uFFFD' ||
                (char.IsControl(c) && c is not ('\t' or '\n' or '\r')))
            {
                return false;
            }
        }

        text = decoded;
        return true;
    }

    private static bool TryDecodeWithBom(byte[] bytes, out string text)
    {
        text = string.Empty;

        // UTF-32 的 BOM 要先于 UTF-16 判断：UTF-32LE 的前缀是 FF FE 00 00
        if (HasPrefix(bytes, [0xFF, 0xFE, 0x00, 0x00]))
        {
            text = Utf32Le.GetString(bytes, 4, bytes.Length - 4);
            return true;
        }

        if (HasPrefix(bytes, [0x00, 0x00, 0xFE, 0xFF]))
        {
            text = Utf32Be.GetString(bytes, 4, bytes.Length - 4);
            return true;
        }

        if (HasPrefix(bytes, [0xEF, 0xBB, 0xBF]))
        {
            text = StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
            return true;
        }

        if (HasPrefix(bytes, [0xFF, 0xFE]))
        {
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return true;
        }

        if (HasPrefix(bytes, [0xFE, 0xFF]))
        {
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return true;
        }

        return false;
    }

    private static bool HasPrefix(byte[] bytes, byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
