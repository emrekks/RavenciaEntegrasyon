using System.Collections.Concurrent;
using System.Security.Claims;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Realtime;

// A SignalR connection is authenticated once, when it opens. Without a check it would keep receiving the
// tenant's events after sign-out, session revocation, a password or authenticator change (SessionVersion),
// the idle timeout or the removal of the membership. HubSessionFilter records each connection's session;
// this monitor checks them all once a minute in one query and aborts the connections whose session is no
// longer good. The browser then reconnects and is refused, or signs in again.
public sealed class HubSessionMonitor(IServiceScopeFactory scopes, TimeProvider time, ILogger<HubSessionMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Tracked> connections = new();

    public int Count => connections.Count;

    public void Track(HubCallerContext context, Guid sessionId) => connections[context.ConnectionId] = new Tracked(context, sessionId);

    public void Forget(string connectionId) => connections.TryRemove(connectionId, out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var aborted = await SweepAsync(stoppingToken);
                    if (aborted > 0) logger.LogInformation("Closed {Count} realtime connections whose session ended.", aborted);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Realtime session check failed; it is retried on the next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        if (connections.IsEmpty) return 0;
        var sessionIds = connections.Values.Select(x => x.SessionId).Distinct().ToArray();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var valid = await (from session in db.UserSessions.AsNoTracking()
                           join user in db.Users.AsNoTracking() on session.UserId equals user.Id
                           where sessionIds.Contains(session.Id) && session.State == SessionState.Active
                               && session.ExpiresAt > now && session.AbsoluteExpiresAt > now
                               && session.SessionVersion == user.SessionVersion && user.Status == "ACTIVE"
                               && db.TenantMemberships.Any(membership => membership.TenantId == session.TenantId && membership.UserId == session.UserId && membership.Status == RecordStatus.Active
                                   && db.Tenants.Any(tenant => tenant.Id == membership.TenantId && tenant.Status == RecordStatus.Active))
                           select session.Id).ToListAsync(cancellationToken);
        return AbortAllExcept(valid.ToHashSet());
    }

    // Aborts and forgets every tracked connection whose session is not among the valid ones.
    internal int AbortAllExcept(IReadOnlySet<Guid> validSessions)
    {
        var aborted = 0;
        foreach (var (connectionId, tracked) in connections)
        {
            if (validSessions.Contains(tracked.SessionId)) continue;
            if (!connections.TryRemove(connectionId, out _)) continue;
            tracked.Context.Abort();
            aborted++;
        }
        return aborted;
    }

    private sealed record Tracked(HubCallerContext Context, Guid SessionId);
}

// Registered for every hub (Program.cs). On connect: a browser connection must come from the panel's own
// origin, and the connection is handed to HubSessionMonitor under its session id; on disconnect it is
// forgotten. Connections without a session claim are left to the hub (OperationsHub aborts them).
public sealed class HubSessionFilter(HubSessionMonitor monitor) : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        if (context.Context.GetHttpContext() is { } http && !RequestSecurityMiddleware.IsSameOrigin(http))
        {
            context.Context.Abort();
            return;
        }
        if (Guid.TryParse(context.Context.User?.FindFirstValue("session_id"), out var sessionId)) monitor.Track(context.Context, sessionId);
        await next(context);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        monitor.Forget(context.Context.ConnectionId);
        await next(context, exception);
    }
}
