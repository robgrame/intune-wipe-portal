using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using IntuneWipePortal.Models;

namespace IntuneWipePortal.Services;

/// <summary>
/// Formati di file supportati per l'export dell'audit trail.
/// </summary>
public enum AuditExportFormat
{
    Csv,
    Xlsx,
    Json,
}

/// <summary>
/// Risultato di una serializzazione di export: i byte del file, il MIME type
/// e l'estensione consigliata per il nome file scaricato.
/// </summary>
public sealed record AuditExportResult(byte[] Content, string ContentType, string FileExtension);

/// <summary>
/// Serializza una lista di <see cref="AuditEventRow"/> nel formato richiesto
/// (CSV, XLSX oppure JSON) per il download dalla pagina Audit. Non effettua
/// alcuna query: riceve già le righe risolte da <see cref="AuditQueryService"/>.
///
/// L'XLSX è generato senza dipendenze esterne scrivendo direttamente un
/// pacchetto OpenXML (SpreadsheetML) con stringhe inline, sufficiente per un
/// foglio tabellare semplice.
/// </summary>
public sealed class AuditExportService
{
    private static readonly string[] Headers =
    {
        "Timestamp (UTC)",
        "Event",
        "CorrelationId",
        "Capability",
        "DeviceName",
        "IntuneDeviceId",
        "EntraDeviceId",
        "CallerUpn",
        "Reason",
        "ExceptionType",
    };

    /// <summary>
    /// Tenta di mappare il valore di una stringa (es. query string) in un
    /// <see cref="AuditExportFormat"/>. Default a <see cref="AuditExportFormat.Csv"/>.
    /// </summary>
    public static AuditExportFormat ParseFormat(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "xlsx" => AuditExportFormat.Xlsx,
        "json" => AuditExportFormat.Json,
        _ => AuditExportFormat.Csv,
    };

    /// <summary>
    /// Serializza <paramref name="rows"/> nel formato indicato.
    /// </summary>
    public AuditExportResult Export(IReadOnlyList<AuditEventRow> rows, AuditExportFormat format) => format switch
    {
        AuditExportFormat.Xlsx => new AuditExportResult(
            BuildXlsx(rows),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "xlsx"),
        AuditExportFormat.Json => new AuditExportResult(
            BuildJson(rows),
            "application/json",
            "json"),
        _ => new AuditExportResult(
            BuildCsv(rows),
            "text/csv",
            "csv"),
    };

    private static string[] ToCells(AuditEventRow r) => new[]
    {
        r.Timestamp.UtcDateTime.ToString("u", CultureInfo.InvariantCulture),
        r.EventName,
        r.CorrelationId,
        r.ActionType ?? "",
        r.DeviceName ?? "",
        r.IntuneDeviceId ?? "",
        r.EntraDeviceId ?? "",
        r.CallerUpn ?? "",
        r.Reason ?? "",
        r.ExceptionType ?? "",
    };

    private static byte[] BuildCsv(IReadOnlyList<AuditEventRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers.Select(EscapeCsv)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", ToCells(r).Select(EscapeCsv)));

        // BOM so Excel apre correttamente UTF-8 con caratteri accentati.
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static string EscapeCsv(string field)
    {
        if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    private static byte[] BuildJson(IReadOnlyList<AuditEventRow> rows)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return JsonSerializer.SerializeToUtf8Bytes(rows, options);
    }

    private static byte[] BuildXlsx(IReadOnlyList<AuditEventRow> rows)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", ContentTypesXml);
            WriteEntry(archive, "_rels/.rels", RootRelsXml);
            WriteEntry(archive, "xl/workbook.xml", WorkbookXml);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(rows));
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildSheetXml(IReadOnlyList<AuditEventRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        AppendRow(sb, 1, Headers);
        var rowIndex = 2;
        foreach (var r in rows)
            AppendRow(sb, rowIndex++, ToCells(r));

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, int rowIndex, IReadOnlyList<string> cells)
    {
        sb.Append("<row r=\"").Append(rowIndex).Append("\">");
        for (var c = 0; c < cells.Count; c++)
        {
            var reference = ColumnName(c + 1) + rowIndex.ToString(CultureInfo.InvariantCulture);
            sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
              .Append(XmlEscape(cells[c]))
              .Append("</t></is></c>");
        }
        sb.Append("</row>");
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        while (index > 0)
        {
            index--;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }
        return name;
    }

    private static string XmlEscape(string value)
    {
        // Rimuove i caratteri di controllo non validi in XML 1.0 e applica l'escaping.
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!(ch is '\t' or '\n' or '\r' || (ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF)))
                continue;

            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private const string ContentTypesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "</Types>";

    private const string RootRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorkbookXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"Audit\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private const string WorkbookRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "</Relationships>";
}
