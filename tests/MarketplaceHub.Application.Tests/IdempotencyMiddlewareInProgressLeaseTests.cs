using System.Text;
using System.Text.Json;
using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-1: a request the process lost (a deploy killed the API mid-request) left its key IN_PROGRESS for 24 hours,
// so the panel's retry answered "still running" all day. The key is now held on a short lease while the request runs
// and settled as UNKNOWN (never run again) once the lease is over; a finished answer is kept for the full replay window.
public sealed class IdempotencyMiddlewareInProgressLeaseTests
{
    [Fact]
    public void TheLeaseOutlastsTheSlowestRequest_AndIsFarShorterThanTheReplayWindow()
    {
        // The size chart's Claude call may run 4 minutes (SizeChartService.CallTimeout).
        Assert.True(IdempotencyMiddleware.InProgressLease > TimeSpan.FromMinutes(4));
        Assert.True(IdempotencyMiddleware.InProgressLease <= TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromHours(24), IdempotencyMiddleware.ReplayWindow);
    }
}

[Collection("postgres-integration")]
public sealed class IdempotencyMiddlewareInProgressLeasePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string Path = "/api/v1/catalog/products";
    private const string Body = "{\"title\":\"lease\"}";
    private static readonly TokenHasher Hasher = new(Enumerable.Repeat((byte)9, 32).ToArray());

    [PostgreSqlFact]
    public async Task ARequestLostLongerThanTheLeaseAgo_IsAnsweredAsUnknown_AndNotRunAgain()
    {
        var (status, code, nextCalled, stored) = await RetryAgainstInProgressAsync("lease-expired", TimeSpan.FromMinutes(11));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("IDEMPOTENCY_RESULT_UNKNOWN", code);
        Assert.Equal("UNKNOWN", stored.State);
    }

    [PostgreSqlFact]
    public async Task ARequestStartedWithinTheLease_IsStillInProgress()
    {
        var (status, code, nextCalled, stored) = await RetryAgainstInProgressAsync("lease-running", TimeSpan.FromMinutes(2));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("IDEMPOTENCY_IN_PROGRESS", code);
        Assert.Equal("IN_PROGRESS", stored.State);
    }

    [PostgreSqlFact]
    public async Task AFinishedRequest_IsKeptForTheFullReplayWindow()
    {
        var tenant = NewTenant("lease-completed");
        await using var db = fixture.CreateContext();
        try
        {
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            var middleware = new IdempotencyMiddleware(http => { http.Response.StatusCode = StatusCodes.Status201Created; return Task.CompletedTask; });
            await middleware.InvokeAsync(NewContext("lease-completed-key"), db, new TenantAccessor(tenant.Id), TimeProvider.System, Hasher);

            await using var check = fixture.CreateContext();
            var stored = await check.ApiIdempotencyRecords.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
            Assert.Equal("COMPLETED", stored.State);
            Assert.True(stored.ExpiresAt > DateTimeOffset.UtcNow.AddHours(23));
        }
        finally
        {
            await CleanUpAsync(tenant.Id);
        }
    }

    private async Task<(int Status, string? Code, bool NextCalled, ApiIdempotencyRecord Stored)> RetryAgainstInProgressAsync(string name, TimeSpan age)
    {
        var tenant = NewTenant(name);
        var key = $"{name}-key";
        await using var db = fixture.CreateContext();
        try
        {
            db.Tenants.Add(tenant);
            var startedAt = DateTimeOffset.UtcNow - age;
            db.ApiIdempotencyRecords.Add(new ApiIdempotencyRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                RouteTemplate = Path,
                IdempotencyKey = IdempotencyRequestDigest.StoredKey(Guid.Empty, key),
                RequestHash = await IdempotencyRequestDigest.RequestHashAsync(Hasher, "POST", Path, string.Empty, string.Empty, "application/json", Encoding.UTF8.GetBytes(Body), CancellationToken.None),
                State = "IN_PROGRESS",
                CreatedAt = startedAt,
                ExpiresAt = startedAt.Add(IdempotencyMiddleware.InProgressLease)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var context = NewContext(key);
            var nextCalled = false;
            var middleware = new IdempotencyMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
            await middleware.InvokeAsync(context, db, new TenantAccessor(tenant.Id), TimeProvider.System, Hasher);

            context.Response.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Response.Body);
            var code = document.RootElement.TryGetProperty("code", out var value) ? value.GetString() : null;
            await using var check = fixture.CreateContext();
            var stored = await check.ApiIdempotencyRecords.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
            return (context.Response.StatusCode, code, nextCalled, stored);
        }
        finally
        {
            await CleanUpAsync(tenant.Id);
        }
    }

    private static DefaultHttpContext NewContext(string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = Path;
        context.Request.ContentType = "application/json";
        context.Request.Headers["Idempotency-Key"] = key;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Tenant NewTenant(string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new Tenant { Id = Guid.CreateVersion7(), Code = $"{name}-{Guid.NewGuid():N}", DisplayName = name, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
    }

    private async Task CleanUpAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.ApiIdempotencyRecords.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class TenantAccessor(Guid tenantId) : ITenantContextAccessor
    {
        public TenantContext? Current { get; } = new(Guid.Empty, tenantId, "ADMIN");
    }
}
