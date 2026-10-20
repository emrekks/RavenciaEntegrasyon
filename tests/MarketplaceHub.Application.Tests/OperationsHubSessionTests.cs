using System.Security.Claims;
using MarketplaceHub.Api.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class OperationsHubSessionTests
{
    [Fact]
    public void Connections_whose_session_is_no_longer_valid_are_closed()
    {
        var registry = new OperationsHubConnections();
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var liveSession = Guid.NewGuid();
        var signedOut = Guid.NewGuid();
        var live = new FakeContext("a");
        var gone = new FakeContext("b");
        var switched = new FakeContext("c");
        registry.Add("a", new(live, liveSession, tenant));
        registry.Add("b", new(gone, signedOut, tenant));
        // Same session, but it now belongs to another tenant: this tenant's changes must stop.
        registry.Add("c", new(switched, liveSession, otherTenant));

        var aborted = registry.AbortExcept(new HashSet<(Guid, Guid)> { (liveSession, tenant) });

        Assert.Equal(2, aborted);
        Assert.False(live.Aborted);
        Assert.True(gone.Aborted);
        Assert.True(switched.Aborted);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void The_session_check_matches_the_request_check()
    {
        using var db = DashboardOrderVisibilityTests.MetadataOnlyContext();
        var sql = OperationsHubSessionWatcher.ValidSessions(db, [Guid.NewGuid()], DateTimeOffset.UtcNow).ToQueryString();

        Assert.Contains("ExpiresAt", sql, StringComparison.Ordinal);
        Assert.Contains("AbsoluteExpiresAt", sql, StringComparison.Ordinal);
        Assert.Contains("SessionVersion", sql, StringComparison.Ordinal);
        Assert.Contains("tenant_memberships", sql, StringComparison.Ordinal);
    }

    private sealed class FakeContext(string id) : HubCallerContext
    {
        public bool Aborted { get; private set; }
        public override string ConnectionId => id;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;
    }
}
