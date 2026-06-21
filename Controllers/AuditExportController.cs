using IntuneWipePortal.Models;
using IntuneWipePortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntuneWipePortal.Controllers;

/// <summary>
/// Endpoint di export dell'audit trail. Consente di scaricare gli eventi di
/// audit scegliendo il formato di file (csv, xlsx oppure json) e il periodo di
/// osservazione. Il nome file proposto (Content-Disposition) permette al browser
/// di chiedere all'utente il percorso di salvataggio.
///
/// Protetto dalla policy <c>CanRead</c> (ruoli Observer/Auditor) come le altre
/// viste di audit.
/// </summary>
[ApiController]
[Route("api/audit")]
[Authorize(Policy = "CanRead")]
public sealed class AuditExportController : ControllerBase
{
    private const int MaxObservationDays = 90;
    private const int MaxRows = 10_000;

    private readonly AuditQueryService _audit;
    private readonly AuditExportService _export;
    private readonly CapabilityRegistry _registry;
    private readonly ILogger<AuditExportController> _log;

    public AuditExportController(
        AuditQueryService audit,
        AuditExportService export,
        CapabilityRegistry registry,
        ILogger<AuditExportController> log)
    {
        _audit = audit;
        _export = export;
        _registry = registry;
        _log = log;
    }

    /// <summary>
    /// Scarica gli eventi di audit.
    /// </summary>
    /// <param name="format">csv | xlsx | json (default csv).</param>
    /// <param name="hours">Periodo di osservazione in ore (default 72, max 90 giorni).</param>
    /// <param name="capability">actionType della capability, oppure vuoto/"__all__" per tutte.</param>
    /// <param name="fileName">Nome file desiderato (senza estensione). Opzionale.</param>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? format,
        [FromQuery] int hours = 72,
        [FromQuery] string? capability = null,
        [FromQuery] string? fileName = null,
        CancellationToken ct = default)
    {
        if (hours <= 0)
            return BadRequest(new { message = "'hours' deve essere maggiore di zero." });

        var maxHours = MaxObservationDays * 24;
        var window = TimeSpan.FromHours(Math.Min(hours, maxHours));

        var cap = await ResolveCapabilityAsync(capability, ct);
        var fmt = AuditExportService.ParseFormat(format);

        IReadOnlyList<AuditEventRow> rows;
        try
        {
            rows = await _audit.GetEventsForExportAsync(window, cap, MaxRows, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Audit export query failed (format={Format}, hours={Hours})", fmt, hours);
            return StatusCode(502, new { message = "Impossibile recuperare gli eventi di audit per l'export." });
        }

        var result = _export.Export(rows, fmt);
        var name = BuildFileName(fileName, result.FileExtension);
        return File(result.Content, result.ContentType, name);
    }

    private async Task<CapabilityDescriptor> ResolveCapabilityAsync(string? capability, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(capability)
            || string.Equals(capability, "__all__", StringComparison.OrdinalIgnoreCase)
            || string.Equals(capability, "all", StringComparison.OrdinalIgnoreCase))
        {
            return CapabilityDescriptor.All;
        }

        var all = await _registry.GetCapabilitiesAsync(ct);
        return all.FirstOrDefault(c =>
                   string.Equals(c.ActionTypeValue, capability, StringComparison.OrdinalIgnoreCase))
               ?? KnownCapabilities.Resolve(capability);
    }

    private static string BuildFileName(string? requested, string extension)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var baseName = string.IsNullOrWhiteSpace(requested) ? $"audit-export-{stamp}" : Sanitize(requested);
        if (string.IsNullOrEmpty(baseName))
            baseName = $"audit-export-{stamp}";
        return $"{baseName}.{extension}";
    }

    /// <summary>
    /// Rimuove caratteri non sicuri da un nome file fornito dall'utente per
    /// evitare path traversal o header injection nel Content-Disposition.
    /// </summary>
    private static string Sanitize(string value)
    {
        var trimmed = value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed
            .Where(c => !invalid.Contains(c) && c != '/' && c != '\\' && !char.IsControl(c))
            .ToArray();
        var name = new string(chars).Trim('.', ' ');
        return name.Length > 120 ? name[..120] : name;
    }
}
