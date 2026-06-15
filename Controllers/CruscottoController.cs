using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntuneWipePortal.Controllers;

/// <summary>
/// Cruscotto operativo — backend API consumata dalla pagina Razor
/// <c>/cruscotto</c>. <b>Stub implementation</b>: ritorna shape JSON
/// corretto ma con dati placeholder cosicché la UI renderizzi. TODO:
/// portare la logica reale (ServiceBus admin + Storage ledger + KQL su
/// LAW) sfruttando il <c>LogsQueryClient</c> e <c>DefaultAzureCredential</c>
/// già configurati in <c>Program.cs</c>. Single source of truth dei DTO è
/// <c>src/Web/Dashboard/DashboardTelemetryService.cs</c> nel repo
/// <c>intune-wipe-api</c>.
/// </summary>
[ApiController]
[Route("api/cruscotto")]
[Authorize(Policy = "CanRead")]
public sealed class CruscottoController : ControllerBase
{
    [HttpGet("data")]
    public IActionResult Data() => Ok(new
    {
        generatedAt = DateTimeOffset.UtcNow,
        queues = new[]
        {
            QueueStub("action-requests"), QueueStub("action-dispatch"),
            QueueStub("wipe-action"),     QueueStub("autopilot-action"),
            QueueStub("bitlocker-action"),QueueStub("rename-action"),
        },
        ledger = new
        {
            status = "gray", totalEntries = 0, stuckEntries = 0,
            oldestStuckIssuedAt = (DateTimeOffset?)null,
            oldestStuckIntuneDeviceId = (string?)null,
            topStuck = Array.Empty<object>(),
            graceHours = 4.0,
        },
        diagnostics = new
        {
            pollerLastTick = (DateTimeOffset?)null,
            pollerHealth = "Unknown",
            capabilityLastSeen = new Dictionary<string, DateTimeOffset?>(),
            issues = new[] { "Backend stub attivo — wirare CruscottoController alle sorgenti reali (SB admin + Storage + KQL)." },
            kqlAvailable = false,
        },
        warnings = Array.Empty<string>(),
    });

    [HttpGet("trace")]
    public IActionResult Trace([FromQuery] string corr) => Ok(new
    {
        correlationId = corr,
        deviceName = (string?)null,
        intuneDeviceId = (string?)null,
        events = Array.Empty<object>(),
        ledgerSummary = (object?)null,
        recommendation = new
        {
            severity = "muted",
            title = "Backend stub attivo",
            detail = "Il portale non è ancora wirato al backend reale. Implementare TraceByCorrelationAsync (KQL su AppEvents+AppExceptions) in CruscottoController.",
            actionKind = "none",
            actionPayload = (string?)null,
        },
    });

    [HttpGet("device")]
    public IActionResult Device([FromQuery] string q, [FromQuery] int take = 25)
        => Ok(new { device = q, rows = Array.Empty<object>() });

    [HttpPost("actions/reset-ledger")]
    [Authorize(Policy = "CanScheduleWrite")] // reset = scrittura, richiede ruolo Operator
    public IActionResult ResetLedger([FromBody] ResetBody body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.IntuneDeviceId) || string.IsNullOrWhiteSpace(body.Reason))
            return BadRequest(new { message = "'intuneDeviceId' e 'reason' sono richiesti" });
        return StatusCode(501, new { message = "Backend stub: implementare il reset chiamando ActionIdempotencyService.ResetAsync via SDK Storage." });
    }

    public sealed record ResetBody(string? IntuneDeviceId, string? Reason);

    private static object QueueStub(string name) => new
    {
        name, active = 0L, deadLetter = 0L, scheduled = 0L,
        accessedAt = (DateTimeOffset?)null,
        status = "gray", error = (string?)null,
    };
}
