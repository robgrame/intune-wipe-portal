using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IntuneWipePortal.Hubs;

[Authorize(Policy = "CanRead")]
public sealed class CruscottoHub : Hub
{
    private readonly ILogger<CruscottoHub> _log;

    public CruscottoHub(ILogger<CruscottoHub> log) => _log = log;

    public override Task OnConnectedAsync()
    {
        _log.LogInformation("SignalR client connected: {ConnectionId}, user: {User}.",
            Context.ConnectionId, Context.User?.Identity?.Name ?? "anonymous");
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
            _log.LogWarning(exception, "SignalR client {ConnectionId} disconnected with error.", Context.ConnectionId);
        else
            _log.LogInformation("SignalR client {ConnectionId} disconnected.", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
