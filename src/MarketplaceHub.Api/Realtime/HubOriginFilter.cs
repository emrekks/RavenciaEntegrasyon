using MarketplaceHub.Api.Security;
using Microsoft.AspNetCore.SignalR;

namespace MarketplaceHub.Api.Realtime;

// Registered for every hub (Program.cs): a browser connection must come from the panel's own origin, like the
// panel's other requests. Which session a connection follows, and closing it once that session ends, is
// OperationsHub's and OperationsHubSessionWatcher's job.
public sealed class HubOriginFilter : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        if (context.Context.GetHttpContext() is { } http && !RequestSecurityMiddleware.IsSameOrigin(http))
        {
            context.Context.Abort();
            return;
        }
        await next(context);
    }
}
