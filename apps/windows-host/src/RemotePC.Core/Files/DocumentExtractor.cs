using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace RemotePC.Core.Files;

public enum ContentKind { Text, Pdf, Word, Spreadsheet, Image, Archive, Binary }

public sealed record ExtractionResult(bool Supported, ContentKind Kind, string Text, bool Truncated, string Message);

public interface IDocumentExtractor
{
    Task<ExtractionResult> ExtractAsync(string path, int maxChars, CancellationToken ct);
}

/// <summary>
/// Turns files into model-readable text. Honest about limits: images are not "read" here (they go to a
/// vision model if one is selected), archives are listed but never extracted, other binaries are refused.
/// </summary>
public sealed class DocumentExtractor : IDocumentExtractor
{
    public const long MaxFileBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".ini", ".toml", ".log", ".cfg", ".conf",
        ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".swift", ".dart", ".go", ".rs", ".c", ".h", ".cpp", ".hpp", ".m",
        ".html", ".htm", ".css", ".scss", ".sql", ".sh", ".ps1", ".bat", ".cmd", ".rb", ".php", ".lua", ".r", ".vue", ".svelte", ".gradle", ".properties", ".csproj", ".sln", ".slnx",
    };
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic" };

    public static ContentKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (TextExt.Contains(ext)) return ContentKind.Text;
        if (ImageExt.Contains(ext)) return ContentKind.Image;
        return ext.ToLowerInvariant() switch
        {
            ".pdf" => ContentKind.Pdf,
            ".docx" => ContentKind.Word,
            ".xlsx" => ContentKind.Spreadsheet,
            ".zip" => ContentKind.Archive,
            _ => LooksLikeText(path) ? ContentKind.Text : ContentKind.Binary,
        };
    }

    private static bool LooksLikeText(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[8192];
            var n = f.Read(buf);
            if (n == 0) return true;
            if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) return false;
            try { new UTF8Encoding(false, true).GetString(buf, 0, n - Math.Min(3, n - 1)); return true; }
            catch (DecoderFallbackException) { return false; }
        }
        catch (IOException) { return false; }
    }

    public async Task<ExtractionResult> ExtractAsync(string path, int maxChars, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return new(false, ContentKind.Binary, "", false, "file not found");
        var kind = Classify(path);
        if (info.Length > MaxFileBytes && kind != ContentKind.Archive)
            return new(false, kind, "", false, $"file is larger than {MaxFileBytes / 1024 / 1024} MB; text extraction skipped");
        try
        {
            switch (kind)
            {
                case ContentKind.Text:
                    using (var r = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    {
                        var buf = new char[maxChars + 1];
                        var n = await r.ReadBlockAsync(buf, ct).ConfigureAwait(false);
                        return Result(kind, new string(buf, 0, Math.Min(n, maxChars)), n > maxChars);
                    }
                case ContentKind.Pdf:
                    return await Task.Run(() =>
                    {
                        var sb = new StringBuilder();
                        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
                        foreach (var page in doc.GetPages())
                        {
                            ct.ThrowIfCancellationRequested();
                            sb.Append("--- page ").Append(page.Number).AppendLine(" ---").AppendLine(page.Text);
                            if (sb.Length > maxChars) break;
                        }
                        if (sb.Length < 20) return new ExtractionResult(false, kind, "", false, "PDF has no extractable text (it may be scanned images; OCR is not supported)");
                        return Result(kind, sb.ToString(), false, maxChars);
                    }, ct).ConfigureAwait(false);
                case ContentKind.Word:
                    {
                        using var doc = WordprocessingDocument.Open(path, false);
                        var body = doc.MainDocumentPart?.Document?.Body;
                        var sb = new StringBuilder();
                        foreach (var p in body?.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>() ?? [])
                        {
                            sb.AppendLine(p.InnerText);
                            if (sb.Length > maxChars) break;
                        }
                        return Result(kind, sb.ToString(), false, maxChars);
                    }
                case ContentKind.Spreadsheet:
                    return Result(kind, ExtractXlsx(path, maxChars), false, maxChars);
                case ContentKind.Archive:
                    {
                        using var zip = ZipFile.OpenRead(path);
                        var sb = new StringBuilder("ZIP archive (listing only; contents are not extracted):\n");
                        foreach (var e in zip.Entries.Take(1000)) sb.Append(e.FullName).Append("  ").Append(e.Length).AppendLine(" bytes");
                        return Result(kind, sb.ToString(), zip.Entries.Count > 1000, maxChars);
                    }
                case ContentKind.Image:
                    return new(false, kind, "", false, "image file: it can be sent to a vision-capable model as an image, but not read as text");
                default:
                    return new(false, kind, "", false, "binary file: it can be stored or transferred, but its content cannot be analysed");
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or FormatException or InvalidOperationException)
        {
            return new(false, kind, "", false, $"could not read {kind.ToString().ToLowerInvariant()} file: {e.Message}");
        }
    }

    private static ExtractionResult Result(ContentKind kind, string text, bool truncated, int maxChars = int.MaxValue)
    {
        if (text.Length > maxChars) { text = text[..maxChars]; truncated = true; }
        return new(true, kind, text, truncated, truncated ? "truncated" : "ok");
    }

    private static string ExtractXlsx(string path, int maxChars)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart ?? throw new InvalidDataException("no workbook");
        var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
        var sb = new StringBuilder();
        foreach (var sheet in wb.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } rid || wb.GetPartById(rid) is not WorksheetPart wsp) continue;
            sb.Append("--- sheet ").Append(sheet.Name).AppendLine(" ---");
            foreach (var row in wsp.Worksheet?.Descendants<Row>() ?? [])
            {
                var cells = row.Elements<Cell>().Select(c =>
                {
                    var v = c.CellValue?.Text ?? c.InnerText;
                    if (c.DataType?.Value == CellValues.SharedString && int.TryParse(v, out var i) && i < shared.Length) v = shared[i];
                    return v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
                });
                sb.AppendLine(string.Join(',', cells));
                if (sb.Length > maxChars) return sb.ToString();
            }
        }
        return sb.ToString();
    }
}
