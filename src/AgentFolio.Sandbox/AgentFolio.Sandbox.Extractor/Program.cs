// AgentFolio.Sandbox.Extractor (spec 002 s4-04): process-isolated extraction
// sandbox for untrusted uploads. Runs OUTSIDE the API process.
//
//   argv[0] = upload file path (input, untrusted bytes)
//   argv[1] = output file path (extracted text destination)
//
// Exit codes: 0 success, 2 bad args / sandbox policy violation, 3 parse error.
// NO filesystem access beyond the two argv paths. NO network. The input and
// output paths must live in the SAME directory (the orchestrator's scratch
// dir) — anything else is rejected before the input is ever opened, so a
// stray path like /etc/passwd is never read.
using System.Text;

namespace AgentFolio.Sandbox.Extractor;

public static class Program
{
    public const long OutputCapBytes = 1 * 1024 * 1024;
    public const string TruncationMarker = "\n[extracted text truncated at 1MB cap]\n";

    private const int ExitOk = 0;
    private const int ExitBadArgs = 2;
    private const int ExitParseError = 3;

    public static int Main(string[] args)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
            return ExitBadArgs;

        string inputPath, outputPath;
        try
        {
            inputPath = Path.GetFullPath(args[0]);
            outputPath = Path.GetFullPath(args[1]);
        }
        catch (Exception)
        {
            return ExitBadArgs;
        }

        // Sandbox policy: both paths must sit in the same scratch directory.
        // Reject before opening anything.
        if (Path.GetDirectoryName(inputPath) != Path.GetDirectoryName(outputPath))
            return ExitBadArgs;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(inputPath);
        }
        catch (Exception)
        {
            return ExitBadArgs;
        }

        string text;
        try
        {
            text = Sniff(bytes) switch
            {
                "pdf" => ExtractPdf(bytes),
                "docx" => ExtractDocx(bytes),
                "txt" => ExtractText(bytes),
                _ => throw new InvalidDataException("unsupported_content_type"),
            };
        }
        catch (Exception)
        {
            return ExitParseError;
        }

        if (Encoding.UTF8.GetByteCount(text) > OutputCapBytes)
            text = TruncateToCap(text);

        try
        {
            File.WriteAllText(outputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception)
        {
            return ExitBadArgs;
        }
        return ExitOk;
    }

    /// <summary>Magic-byte sniffing — mirrors UploadService.Sniff.</summary>
    public static string? Sniff(byte[] bytes)
    {
        if (bytes.Length >= 5 && bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            return "pdf";
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("PK\x03\x04"u8))
            return "docx";
        if (bytes.Length > 0 && IsPlausiblePlainText(bytes))
            return "txt";
        return null;
    }

    private static bool IsPlausiblePlainText(byte[] bytes)
    {
        var strictUtf8 = (System.Text.Encoding)System.Text.Encoding.UTF8.Clone();
        strictUtf8.DecoderFallback = System.Text.DecoderFallback.ExceptionFallback;
        try
        {
            _ = strictUtf8.GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
        foreach (var b in bytes)
        {
            if (b == 0) return false;
        }
        return true;
    }

    private static string ExtractText(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    private static string ExtractDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("docx_missing_document_xml");
        using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
        var xml = reader.ReadToEnd();
        return StripXmlTags(xml);
    }

    internal static string StripXmlTags(string xml)
    {
        // Paragraph/close-tag boundaries become newlines; remaining tags stripped;
        // entities decoded. XML parsers are avoided on untrusted input.
        var sb = new System.Text.StringBuilder(xml.Length);
        bool inTag = false;
        foreach (var ch in xml)
        {
            if (ch == '<') { inTag = true; if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n'); }
            else if (ch == '>') inTag = false;
            else if (!inTag) sb.Append(ch);
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }

    /// <summary>
    /// Naive PDF text extraction (v1.1): Latin-1 decode of the raw bytes, then
    /// pull literal strings out of text-showing operators (Tj / TJ) inside
    /// BT..ET blocks. Good enough for simple text PDFs; unavailable PdfPig
    /// would be required for embedded-font/encoded PDFs.
    /// </summary>
    internal static string ExtractPdf(byte[] bytes)
    {
        var raw = System.Text.Encoding.Latin1.GetString(bytes);
        var sb = new System.Text.StringBuilder();
        foreach (var op in SplitOperators(raw))
        {
            if (op.Contains("Tj") || op.Contains("TJ"))
                AppendLiteralStrings(op, sb);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> SplitOperators(string raw)
    {
        int bt = 0;
        while ((bt = raw.IndexOf("BT", bt, StringComparison.Ordinal)) >= 0)
        {
            var et = raw.IndexOf("ET", bt, StringComparison.Ordinal);
            if (et < 0) yield break;
            yield return raw[bt..et];
            bt = et + 2;
        }
    }

    private static void AppendLiteralStrings(string op, System.Text.StringBuilder sb)
    {
        int i = 0;
        while (i < op.Length)
        {
            if (op[i] != '(') { i++; continue; }
            i++;
            var start = i;
            int depth = 1;
            while (i < op.Length && depth > 0)
            {
                var c = op[i];
                if (c == '\\' && i + 1 < op.Length) { i += 2; continue; }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                if (depth > 0) i++;
            }
            sb.Append(UnescapePdfString(op[start..Math.Min(i, op.Length)]));
            sb.Append('\n');
        }
    }

    private static string UnescapePdfString(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    var c => c,
                });
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    private static string TruncateToCap(string text)
    {
        // Cut by encoded bytes, then append the marker.
        var encoder = System.Text.Encoding.UTF8;
        var maxBytes = (int)(OutputCapBytes - encoder.GetByteCount(TruncationMarker));
        if (maxBytes <= 0) return TruncationMarker;
        var all = encoder.GetBytes(text);
        var slice = all.AsSpan(0, maxBytes).ToArray();
        var decoded = encoder.GetString(slice);
        // Trim a possibly-split trailing multi-byte char.
        if (decoded.Length > 0 && char.IsHighSurrogate(decoded[^1]) && decoded.Length == text.Length)
            decoded = decoded[..^1];
        return decoded.TrimEnd('\uFFFD') + TruncationMarker;
    }
}
