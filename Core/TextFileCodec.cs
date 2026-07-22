using System.Security.Cryptography;
using System.Text;

namespace CodexChannelLauncher.Core;

public sealed record TextFileSnapshot(
    bool Exists,
    bool IsText,
    string Text,
    int CodePage,
    bool EmitPreamble,
    string NewLine,
    long Length,
    string Fingerprint,
    string EncodingDisplayName,
    string FailureReason)
{
    public static TextFileSnapshot Missing { get; } = new(
        false,
        true,
        string.Empty,
        Encoding.UTF8.CodePage,
        false,
        "\r\n",
        0,
        TextFileCodec.MissingFingerprint,
        "不存在",
        string.Empty);
}

public static class TextFileCodec
{
    public const string MissingFingerprint = "<missing>";
    private const int MaximumInteractiveBytes = 2 * 1024 * 1024;

    static TextFileCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static TextFileSnapshot Read(string path)
    {
        if (!File.Exists(path))
        {
            return TextFileSnapshot.Missing;
        }

        var info = new FileInfo(path);
        var fingerprint = ComputeSha256(path);
        if (info.Length > MaximumInteractiveBytes)
        {
            return new TextFileSnapshot(
                true,
                false,
                string.Empty,
                Encoding.UTF8.CodePage,
                false,
                "\r\n",
                info.Length,
                fingerprint,
                "整文件模式",
                $"文件超过 {MaximumInteractiveBytes / 1024 / 1024} MB，逐块编辑已关闭。");
        }

        var bytes = File.ReadAllBytes(path);
        if (!TryDecode(bytes, out var text, out var encoding, out var emitPreamble, out var displayName))
        {
            return new TextFileSnapshot(
                true,
                false,
                string.Empty,
                Encoding.UTF8.CodePage,
                false,
                "\r\n",
                info.Length,
                fingerprint,
                "二进制",
                "文件不是受支持的文本编码，只能整文件采用。");
        }

        var newLine = DetectNewLine(text);
        return new TextFileSnapshot(
            true,
            true,
            NormalizeNewLines(text),
            encoding.CodePage,
            emitPreamble,
            newLine,
            info.Length,
            fingerprint,
            displayName,
            string.Empty);
    }

    public static byte[] Encode(string normalizedText, TextFileSnapshot template)
    {
        var encoding = Encoding.GetEncoding(
            template.CodePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        var text = NormalizeNewLines(normalizedText).Replace("\n", template.NewLine, StringComparison.Ordinal);
        var body = encoding.GetBytes(text);
        if (!template.EmitPreamble)
        {
            return body;
        }

        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0)
        {
            return body;
        }

        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    public static string ComputeFingerprint(string path) =>
        File.Exists(path) ? ComputeSha256(path) : MissingFingerprint;

    public static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static bool TryDecode(
        byte[] bytes,
        out string text,
        out Encoding encoding,
        out bool emitPreamble,
        out string displayName)
    {
        text = string.Empty;
        encoding = new UTF8Encoding(false, true);
        emitPreamble = false;
        displayName = "UTF-8";

        try
        {
            var offset = 0;
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            {
                encoding = new UTF8Encoding(true, true);
                emitPreamble = true;
                displayName = "UTF-8 BOM";
                offset = Encoding.UTF8.GetPreamble().Length;
            }
            else if (bytes.AsSpan().StartsWith(Encoding.UTF32.GetPreamble()))
            {
                encoding = new UTF32Encoding(false, true, true);
                emitPreamble = true;
                displayName = "UTF-32 LE";
                offset = Encoding.UTF32.GetPreamble().Length;
            }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            {
                encoding = new UTF32Encoding(true, true, true);
                emitPreamble = true;
                displayName = "UTF-32 BE";
                offset = 4;
            }
            else if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
            {
                encoding = new UnicodeEncoding(false, true, true);
                emitPreamble = true;
                displayName = "UTF-16 LE";
                offset = Encoding.Unicode.GetPreamble().Length;
            }
            else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            {
                encoding = new UnicodeEncoding(true, true, true);
                emitPreamble = true;
                displayName = "UTF-16 BE";
                offset = Encoding.BigEndianUnicode.GetPreamble().Length;
            }
            else
            {
                if (bytes.Contains((byte)0))
                {
                    return false;
                }

                encoding = new UTF8Encoding(false, true);
            }

            text = encoding.GetString(bytes, offset, bytes.Length - offset);
            return LooksLikeText(text);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                if (bytes.Contains((byte)0))
                {
                    return false;
                }

                encoding = Encoding.GetEncoding(
                    54936,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                text = encoding.GetString(bytes);
                emitPreamble = false;
                displayName = "GB18030";
                return LooksLikeText(text);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }
    }

    private static bool LooksLikeText(string text)
    {
        if (text.IndexOf('\0') >= 0)
        {
            return false;
        }

        if (text.Length == 0)
        {
            return true;
        }

        var suspicious = text.Count(character =>
            char.IsControl(character) && character is not '\r' and not '\n' and not '\t' and not '\f');
        return suspicious <= Math.Max(2, text.Length / 100);
    }

    private static string DetectNewLine(string text)
    {
        var crlf = Count(text, "\r\n");
        var withoutCrlf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        var lf = withoutCrlf.Count(character => character == '\n');
        var cr = withoutCrlf.Count(character => character == '\r');
        if (crlf >= lf && crlf >= cr && crlf > 0)
        {
            return "\r\n";
        }

        return cr > lf ? "\r" : "\n";
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
