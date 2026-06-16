using IntuneWipePortal.Services;
using IntuneWipePortal.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace IntuneWipePortal.Controllers;

/// <summary>
/// Cruscotto operativo — backend API consumata dalla pagina Razor
/// <c>/cruscotto</c>. Delega tutto a <see cref="CruscottoTelemetryService"/>
/// che parla con ServiceBus admin, blob ledger e KQL (LAW) tramite la UAMI
/// del portale.
///
/// <para>Reset del ledger NON implementato qui: l'operazione resta esposta
/// dall'endpoint admin del Web Function App lato API (mTLS + thumbprint
/// allow-list) per preservare la separazione di responsabilità.</para>
/// </summary>
[ApiController]
[Route("api/cruscotto")]
[Authorize(Policy = "CanRead")]
public sealed class CruscottoController : ControllerBase
{
    private readonly CruscottoTelemetryService _svc;
    public CruscottoController(CruscottoTelemetryService svc) => _svc = svc;

    [HttpGet("data")]
    public async Task<IActionResult> Data(CancellationToken ct)
        => Ok(await _svc.SnapshotAsync(ct));

    [HttpGet("trace")]
    public async Task<IActionResult> Trace([FromQuery] string corr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(corr))
            return BadRequest(new { message = "Query parameter 'corr' is required." });
        return Ok(await _svc.TraceByCorrelationAsync(corr, ct));
    }

    [HttpGet("device")]
    public async Task<IActionResult> Device([FromQuery] string q, [FromQuery] int take = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { message = "Query parameter 'q' is required." });
        var rows = await _svc.RecentByDeviceAsync(q, take, ct);
        return Ok(new { device = q, rows });
    }

    [HttpPost("actions/reset-ledger")]
    [Authorize(Policy = "CanScheduleWrite")]
    public IActionResult ResetLedger([FromBody] ResetBody body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.IntuneDeviceId) || string.IsNullOrWhiteSpace(body.Reason))
            return BadRequest(new { message = "'intuneDeviceId' e 'reason' sono richiesti" });
        return StatusCode(501, new
        {
            message = "Reset ledger non implementato dal portale. Usare l'endpoint admin del Web Function API (mTLS) per preservare la separazione di responsabilità.",
        });
    }

    [HttpPost("actions/purge-queue")]
    [Authorize(Policy = "CanScheduleWrite")]
    public async Task<IActionResult> PurgeQueue([FromBody] PurgeQueueBody body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.QueueName))
            return BadRequest(new { message = "'queueName' è richiesto" });

        try
        {
            var max = body.MaxMessages is > 0 ? body.MaxMessages.Value : 500;
            var result = await _svc.PurgeQueueAsync(body.QueueName, max, body.DeadLetter, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("queues/{queueName}/peek")]
    [Authorize(Policy = "CanRead")]
    public async Task<IActionResult> PeekQueue([FromRoute] string queueName, [FromQuery] int top = 10, [FromQuery] bool deadLetter = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(queueName))
            return BadRequest(new { message = "'queueName' è richiesto" });

        try
        {
            var result = await _svc.PeekQueueAsync(queueName, top, deadLetter, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("actions/restart-function")]
    [Authorize(Policy = "CanScheduleWrite")]
    public async Task<IActionResult> RestartFunction([FromBody] RestartFunctionBody body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.FunctionAppName))
            return BadRequest(new { message = "'functionAppName' è richiesto" });

        try
        {
            var result = await _svc.RestartFunctionAppAsync(body.FunctionAppName, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    public sealed record ResetBody(string? IntuneDeviceId, string? Reason);
    public sealed record PurgeQueueBody(string? QueueName, int? MaxMessages, bool DeadLetter = false);
    public sealed record RestartFunctionBody(string? FunctionAppName);

    [HttpPost("eventgrid/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> EventGridWebhook(
        [FromBody] JsonElement eventsPayload,
        [FromServices] IHubContext<CruscottoHub> hub,
        [FromServices] EventGridMetricsCollector metrics,
        CancellationToken ct)
    {
        var aegType = Request.Headers["aeg-event-type"].ToString();
        if (string.Equals(aegType, "SubscriptionValidation", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetValidationCode(eventsPayload, out var code))
            {
                return Ok(new { validationResponse = code });
            }

            return BadRequest(new { message = "subscription validation payload missing validationCode" });
        }

        if (string.Equals(aegType, "Notification", StringComparison.OrdinalIgnoreCase))
        {
            metrics.Ingest(eventsPayload);
            var snapshot = await _svc.SnapshotAsync(ct);
            await hub.Clients.All.SendAsync("snapshot", snapshot, cancellationToken: ct);
            return Ok(new { accepted = true });
        }

        return Ok(new { accepted = true });
    }

    private static bool TryGetValidationCode(JsonElement payload, out string? validationCode)
    {
        validationCode = null;
        if (payload.ValueKind != JsonValueKind.Array || payload.GetArrayLength() == 0)
            return false;

        var first = payload[0];
        if (!first.TryGetProperty("data", out var data))
            return false;

        if (!data.TryGetProperty("validationCode", out var codeNode))
            return false;

        validationCode = codeNode.GetString();
        return !string.IsNullOrWhiteSpace(validationCode);
    }
}
