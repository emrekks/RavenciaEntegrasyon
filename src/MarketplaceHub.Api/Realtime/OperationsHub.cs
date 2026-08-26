using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace MarketplaceHub.Api.Realtime;

public sealed class OperationsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirstValue("tenant_id");
        if (!Guid.TryParse(tenantId, out _))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId));
        await base.OnConnectedAsync();
    }

    public static string TenantGroup(string tenantId) => $"tenant:{tenantId}";
}
