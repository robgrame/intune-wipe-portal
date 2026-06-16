using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IntuneWipePortal.Hubs;

[Authorize(Policy = "CanRead")]
public sealed class CruscottoHub : Hub
{
}
