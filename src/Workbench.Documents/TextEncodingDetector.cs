using System.Text;

namespace Workbench.Documents;

/// <summary>
/// 纯文本编码探测。
///
/// 需求文档绝大多数是 UTF-8，但也有 GBK / GB18030 的老文件。
/// 直接按 UTF-8 解码会把中文变成一串 ""，agent 读到的全是乱码。
///
/// 回退顺序：BOM → 严格 UTF-8 → GB18030 → UTF-8 替换字符。
/// 严格 UTF-8 这一步很关键：能过就说明本来就是 UTF-8，不用去猜。
/// </summary>
public static class TextEncodingDetector
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF32Encoding Utf32Le = new(bigEndian: false, byteOrderMark: false);
    private static readonly UTF32Encoding Utf32Be = new(bigEndian: true, byteOrderMark: false);

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

        // 2. 严格 UTF-8：过了就是 UTF-8
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8，继续往下试
        }

        // 3. GB18030：中文 Windows 上最常见的非 UTF-8 编码
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
