using IntuneWipePortal.Models;

namespace IntuneWipePortal.Services;

/// <summary>
/// Parses an operator-pasted CSV / TSV blob into rows ready for
/// <see cref="WipeScheduleService.AddMembersBulkAsync"/>. Designed for the
/// "bulk import" UX where an admin pastes a list of devices exported from
/// Intune / Entra and we need to add hundreds-to-thousands of memberships
/// in one click.
///
/// Supported formats (auto-detected):
/// <list type="bullet">
///   <item><description>Header row optional. Header is detected when the
///   first row contains the literal tokens <c>deviceName</c>,
///   <c>entraDeviceId</c> (case-insensitive, in any order) and is then
///   used to resolve column ordering.</description></item>
///   <item><description>Separator: <c>,</c>, <c>;</c> or tab — sniffed from
///   the first non-empty line.</description></item>
///   <item><description>Without a header: column order is
///   <c>deviceName, entraDeviceId, intuneDeviceId?</c>.</description></item>
///   <item><description>Single-column input: each line is treated as an
///   Entra device id and <c>deviceName</c> is auto-filled with the same
///   GUID (operator can edit later from the UI).</description></item>
///   <item><description>Lines starting with <c>#</c> and empty lines are
///   ignored.</description></item>
/// </list>
/// </summary>
public static class BulkImportParser
{
    public static BulkImportResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new BulkImportResult(Array.Empty<BulkImportRow>(), 0);

        var lines = input
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        if (lines.Count == 0)
            return new BulkImportResult(Array.Empty<BulkImportRow>(), 0);

        var sep = SniffSeparator(lines[0]);
        int startIdx = 0;
        int nameCol = 0, entraCol = 1, intuneCol = 2;

        // Header detection — only if at least one of the canonical tokens
        // is present and the row clearly isn't a data row (no GUID).
        var firstParts = SplitLine(lines[0], sep);
        if (LooksLikeHeader(firstParts))
        {
            startIdx = 1;
            nameCol  = FindColumn(firstParts, "devicename", "name");
            entraCol = FindColumn(firstParts, "entradeviceid", "entraid", "aadid", "deviceid");
            intuneCol = FindColumn(firstParts, "intunedeviceid", "intuneid", "managedid");
            if (entraCol < 0)
            {
                return new BulkImportResult(
                    Array.Empty<BulkImportRow>(),
                    Total: lines.Count - 1,
                    HeaderError: "Header row is missing the 'entraDeviceId' column.");
            }
            if (nameCol < 0) nameCol = entraCol; // fallback: reuse the id as the name
        }

        var rows = new List<BulkImportRow>(lines.Count - startIdx);
        for (int i = startIdx; i < lines.Count; i++)
        {
            var parts = SplitLine(lines[i], sep);

            // Single-column shortcut: treat the value as the Entra id.
            string? rawName, rawEntra, rawIntune = null;
            if (parts.Length == 1)
            {
                rawEntra = parts[0];
                rawName  = parts[0];
            }
            else
            {
                rawName  = SafeGet(parts, nameCol);
                rawEntra = SafeGet(parts, entraCol);
                rawIntune = intuneCol >= 0 ? SafeGet(parts, intuneCol) : null;
            }

            string? err = null;
            Guid id = Guid.Empty;
            if (string.IsNullOrWhiteSpace(rawEntra))
                err = "Missing Entra device id.";
            else if (!Guid.TryParse(rawEntra, out id))
                err = $"'{rawEntra}' is not a valid GUID.";
            else if (string.IsNullOrWhiteSpace(rawName))
                err = "Missing device name.";

            rows.Add(new BulkImportRow(
                LineNumber: i + 1,
                DeviceName: rawName?.Trim() ?? string.Empty,
                EntraDeviceIdRaw: rawEntra?.Trim() ?? string.Empty,
                EntraDeviceId: id,
                IntuneDeviceId: string.IsNullOrWhiteSpace(rawIntune) ? null : rawIntune!.Trim(),
                Error: err));
        }

        return new BulkImportResult(rows, rows.Count);
    }

    /// <summary>Returns only the rows that are valid (no <c>Error</c>),
    /// projected to the shape expected by the service layer.</summary>
    public static IEnumerable<BulkMemberInput> ValidInputs(IEnumerable<BulkImportRow> rows) =>
        rows.Where(r => r.Error is null)
            .Select(r => new BulkMemberInput(r.DeviceName, r.EntraDeviceId, r.IntuneDeviceId));

    // --- helpers ----------------------------------------------------------

    private static char SniffSeparator(string firstLine)
    {
        // Honour the highest-frequency separator. Default to comma so
        // single-column input still parses cleanly.
        var counts = new[] { (',', firstLine.Count(c => c == ',')),
                             (';', firstLine.Count(c => c == ';')),
                             ('\t', firstLine.Count(c => c == '\t')) };
        var best = counts.OrderByDescending(x => x.Item2).First();
        return best.Item2 > 0 ? best.Item1 : ',';
    }

    private static string[] SplitLine(string line, char sep)
    {
        // Lightweight quoted-field handling: support "value with , inside"
        // because Intune's "Export to CSV" wraps strings in double quotes.
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == sep && !inQuotes) { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        parts.Add(current.ToString());
        return parts.Select(p => p.Trim()).ToArray();
    }

    private static bool LooksLikeHeader(string[] parts)
    {
        var lowered = parts.Select(p => p.ToLowerInvariant()).ToArray();
        return lowered.Any(p => p == "devicename" || p == "entradeviceid" ||
                                p == "aadid"      || p == "entraid"      ||
                                p == "intunedeviceid")
               && !lowered.Any(p => Guid.TryParse(p, out _));
    }

    private static int FindColumn(string[] header, params string[] aliases)
    {
        for (int i = 0; i < header.Length; i++)
        {
            var h = header[i].ToLowerInvariant();
            if (aliases.Contains(h, StringComparer.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static string? SafeGet(string[] parts, int idx) =>
        idx >= 0 && idx < parts.Length ? parts[idx] : null;
}

/// <summary>
/// One parsed row from a bulk import. <see cref="Error"/> is non-null when
/// the row is invalid (the UI surfaces it as a per-row badge).
/// </summary>
public sealed record BulkImportRow(
    int LineNumber,
    string DeviceName,
    string EntraDeviceIdRaw,
    Guid EntraDeviceId,
    string? IntuneDeviceId,
    string? Error);

/// <summary>
/// Aggregate parse outcome. <see cref="HeaderError"/> is set only when the
/// CSV declared a header that did NOT include the required
/// <c>entraDeviceId</c> column — in which case parsing of data rows is
/// skipped entirely.
/// </summary>
public sealed record BulkImportResult(
    IReadOnlyList<BulkImportRow> Rows,
    int Total,
    string? HeaderError = null)
{
    public int ValidCount   => Rows.Count(r => r.Error is null);
    public int InvalidCount => Rows.Count(r => r.Error is not null);
}
