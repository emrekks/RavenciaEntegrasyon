using System.Collections.Concurrent;
using System.Security.Claims;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Realtime;

public sealed class OperationsHub(OperationsHubConnections connections) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirstValue("tenant_id");
        var sessionId = Context.User?.FindFirstValue("session_id");
        if (!Guid.TryParse(tenantId, out var tenant) || !Guid.TryParse(sessionId, out var session))
        {
            Context.Abort();
            return;
        }
        // The connection follows the sign-in session it was opened with (OperationsHubSessionWatcher).
        connections.Add(Context.ConnectionId, new(Context, session, tenant));
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId!));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public static string TenantGroup(string tenantId) => $"tenant:{tenantId}";
}

// The open realtime connections of this API instance and the session each was opened with.
public sealed class OperationsHubConnections
{
    public sealed record Entry(HubCallerContext Context, Guid SessionId, Guid TenantId);

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public void Add(string connectionId, Entry entry) => entries[connectionId] = entry;
    public void Remove(string connectionId) => entries.TryRemove(connectionId, out _);
    public IReadOnlyList<KeyValuePair<string, Entry>> Snapshot() => entries.ToArray();
    public int Count => entries.Count;

    // Closes every connection whose session is no longer valid for its tenant. Returns how many were closed.
    public int AbortExcept(IReadOnlySet<(Guid SessionId, Guid TenantId)> valid)
    {
        var aborted = 0;
        foreach (var (connectionId, entry) in Snapshot())
        {
            if (valid.Contains((entry.SessionId, entry.TenantId))) continue;
            entries.TryRemove(connectionId, out _);
            entry.Context.Abort();
            aborted++;
        }
        return aborted;
    }
}

// Signing out, a revoked or expired session, a user switched to another tenant or removed from it: a realtime
// connection opened before must not keep receiving that tenant's changes. Once a minute the sessions behind the
// open connections are checked the way every HTTP request checks them, and the others are closed; the browser's
// reconnect then meets the sign-in screen.
public sealed class OperationsHubSessionWatcher(OperationsHubConnections connections, IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<OperationsHubSessionWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (connections.Count == 0) continue;
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var open = connections.Snapshot().Select(x => x.Value.SessionId).Distinct().ToArray();
                var valid = await ValidSessions(db, open, timeProvider.GetUtcNow()).ToListAsync(stoppingToken);
                var aborted = connections.AbortExcept(valid.Select(x => (x.SessionId, x.TenantId)).ToHashSet());
                if (aborted > 0) logger.LogInformation("Oturumu sona eren canlı bağlantılar kapatıldı. Kapatılan: {Aborted}", aborted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Canlı bağlantıların oturumları denetlenemedi; sonraki turda denenecek");
            }
        }
    }

    internal sealed record ValidSession(Guid SessionId, Guid TenantId);

    // The same conditions SessionAuthMiddleware applies to a request that carries a tenant.
    internal static IQueryable<ValidSession> ValidSessions(AppDbContext db, Guid[] sessionIds, DateTimeOffset now) =>
        from session in db.UserSessions.AsNoTracking()
        join user in db.Users.AsNoTracking() on session.UserId equals user.Id
        where sessionIds.Contains(session.Id)
            && session.State == SessionState.Active
            && session.TenantId != null
            && session.ExpiresAt > now
            && session.AbsoluteExpiresAt > now
            && user.SessionVersion == session.SessionVersion
            && user.Status == "ACTIVE"
            && db.TenantMemberships.Any(membership => membership.TenantId == session.TenantId && membership.UserId == session.UserId && membership.Status == RecordStatus.Active
                && db.Tenants.Any(tenant => tenant.Id == membership.TenantId && tenant.Status == RecordStatus.Active))
        select new ValidSession(session.Id, session.TenantId!.Value);
}
