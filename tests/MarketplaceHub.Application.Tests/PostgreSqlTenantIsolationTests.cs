using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PostgreSqlTenantIsolationTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task OperationalIssueDedupeKey_AllowsSameKeyAcrossTenants_ButRejectsDuplicateWithinTenant()
    {
        var firstTenant = NewTenant("tenant-a");
        var secondTenant = NewTenant("tenant-b");
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        {
            db.Tenants.AddRange(firstTenant, secondTenant);
            db.OperationalIssues.AddRange(
                NewIssue(firstTenant.Id, "same-key"),
                NewIssue(secondTenant.Id, "same-key"));
            await db.SaveChangesAsync();

            db.ChangeTracker.Clear();
            db.OperationalIssues.Add(NewIssue(firstTenant.Id, "same-key"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task SessionForDisabledTenant_DoesNotReceiveTenantClaims()
    {
        var tenant = NewTenant("disabled-tenant");
        tenant.Status = RecordStatus.Disabled;
        var user = NewUser();
        var membership = NewMembership(tenant.Id, user.Id, MembershipRole.Administrator);
        var rawToken = TokenHasher.NewToken();
        var session = NewSession(user.Id, tenant.Id, rawToken, fixture.Now);

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        {
            db.Tenants.Add(tenant);
            db.Users.Add(user);
            db.TenantMemberships.Add(membership);
            db.UserSessions.Add(session);
            await db.SaveChangesAsync();

            var context = NewHttpContext(rawToken);
            var called = false;
            var middleware = new SessionAuthMiddleware(_ =>
            {
                called = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context, db, fixture.TokenHasher, fixture.TimeProvider);

            Assert.True(called);
            Assert.True(context.User.Identity?.IsAuthenticated);
            Assert.Null(context.User.FindFirstValue("tenant_id"));
            Assert.Null(context.User.FindFirstValue(ClaimTypes.Role));
        }
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task SessionForActiveTenant_ReceivesTenantAndRoleClaims()
    {
        var tenant = NewTenant("active-tenant");
        var user = NewUser();
        var membership = NewMembership(tenant.Id, user.Id, MembershipRole.Operations);
        var rawToken = TokenHasher.NewToken();
        var session = NewSession(user.Id, tenant.Id, rawToken, fixture.Now);

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        {
            db.Tenants.Add(tenant);
            db.Users.Add(user);
            db.TenantMemberships.Add(membership);
            db.UserSessions.Add(session);
            await db.SaveChangesAsync();

            var context = NewHttpContext(rawToken);
            var middleware = new SessionAuthMiddleware(_ => Task.CompletedTask);

            await middleware.InvokeAsync(context, db, fixture.TokenHasher, fixture.TimeProvider);

            Assert.Equal(tenant.Id.ToString(), context.User.FindFirstValue("tenant_id"));
            Assert.Equal("OPERATIONS", context.User.FindFirstValue(ClaimTypes.Role));
        }
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task JobLease_IsExclusive_WhenTwoWorkersRace()
    {
        var tenant = NewTenant("lease-tenant");
        var job = NewJob(tenant.Id);

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        try
        {
            LeasedJob?[] leases;
            await using (var firstDb = fixture.CreateContext())
            await using (var secondDb = fixture.CreateContext())
            {
                var firstWorker = new JobLeaseService(firstDb, fixture.TokenHasher, fixture.TimeProvider);
                var secondWorker = new JobLeaseService(secondDb, fixture.TokenHasher, fixture.TimeProvider);
                leases = await Task.WhenAll(
                    firstWorker.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None),
                    secondWorker.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None));
            }

            Assert.Single(leases, lease => lease is not null);
            Assert.Single(leases, lease => lease is null);

            await using var verification = fixture.CreateContext();
            var persisted = await verification.IntegrationJobs.SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobStatus.Leased, persisted.Status);
            Assert.Equal(1, persisted.AttemptCount);
            Assert.Equal(1, await verification.JobAttempts.CountAsync(x => x.JobId == job.Id));
        }
        finally
        {
            await DeleteJobAndTenantAsync(job.Id, tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ApiIdempotencyKey_IsScopedToTenant_ButDuplicateWithinTenantIsRejected()
    {
        var firstTenant = NewTenant("idempotency-a");
        var secondTenant = NewTenant("idempotency-b");
        const string route = "/api/v1/catalog/products";
        const string key = "same-idempotency-key";

        await using var db = fixture.CreateContext();
        try
        {
            db.Tenants.AddRange(firstTenant, secondTenant);
            db.ApiIdempotencyRecords.AddRange(
                NewIdempotencyRecord(firstTenant.Id, route, key),
                NewIdempotencyRecord(secondTenant.Id, route, key));
            await db.SaveChangesAsync();

            db.ChangeTracker.Clear();
            db.ApiIdempotencyRecords.Add(NewIdempotencyRecord(firstTenant.Id, route, key));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await DeleteIdempotencyRecordsAndTenantsAsync(firstTenant.Id, secondTenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task IdempotencyMiddleware_ReplaysCompletedResponseFromPostgreSql()
    {
        var tenant = NewTenant("middleware-replay");
        const string path = "/api/v1/catalog/products";
        const string key = "middleware-replay-key";
        const string requestBody = "{}";
        const string responseBody = "{\"id\":\"replayed\"}";

        await using var db = fixture.CreateContext();
        try
        {
            db.Tenants.Add(tenant);
            var record = NewIdempotencyRecord(tenant.Id, path, key);
            record.RequestHash = ComputeRequestHash("POST", path, string.Empty, requestBody);
            record.State = "COMPLETED";
            record.ResponseStatus = StatusCodes.Status201Created;
            record.ResponseBody = responseBody;
            db.ApiIdempotencyRecords.Add(record);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var context = NewIdempotencyHttpContext(path, key, requestBody);
            var nextCalled = false;
            var middleware = new IdempotencyMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context, db, new FixedTenantContextAccessor(tenant.Id), fixture.TimeProvider);

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);
            Assert.Equal("true", context.Response.Headers["Idempotency-Replayed"].ToString());
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
            Assert.Equal(responseBody, await reader.ReadToEndAsync());
        }
        finally
        {
            await DeleteIdempotencyRecordsAndTenantsAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task IdempotencyMiddleware_PersistsUnknownAfterEndpointFailure()
    {
        var tenant = NewTenant("middleware-unknown");
        const string path = "/api/v1/catalog/products";
        const string key = "middleware-unknown-key";

        await using var db = fixture.CreateContext();
        try
        {
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();

            var context = NewIdempotencyHttpContext(path, key, "{\"title\":\"test\"}");
            var middleware = new IdempotencyMiddleware(_ => throw new InvalidOperationException("simulated endpoint failure"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(
                context,
                db,
                new FixedTenantContextAccessor(tenant.Id),
                fixture.TimeProvider));

            db.ChangeTracker.Clear();
            var persisted = await db.ApiIdempotencyRecords.SingleAsync(x => x.TenantId == tenant.Id && x.RouteTemplate == path && x.IdempotencyKey == key);
            Assert.Equal("UNKNOWN", persisted.State);
            Assert.Null(persisted.ResponseStatus);
            Assert.Null(persisted.ResponseBody);
            Assert.True(persisted.ExpiresAt > fixture.Now.AddDays(6));
        }
        finally
        {
            await DeleteIdempotencyRecordsAndTenantsAsync(tenant.Id);
        }
    }

    private static DefaultHttpContext NewHttpContext(string rawToken)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["MARKETPLACEHUB_ENVIRONMENT"] = "PILOT_LOCAL" })
                    .Build())
                .BuildServiceProvider()
        };
        context.Request.Scheme = "http";
        context.Request.Headers.Cookie = $"{SessionAuthMiddleware.PilotLocalCookieName}={rawToken}";
        return context;
    }

    private static Tenant NewTenant(string code) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = $"{code}-{Guid.NewGuid():N}",
        DisplayName = code,
        Status = RecordStatus.Active,
        Timezone = "Europe/Istanbul",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Version = 1
    };

    private static ApplicationUser NewUser()
    {
        var id = Guid.CreateVersion7();
        return new ApplicationUser
        {
            Id = id,
            UserName = $"test-{id:N}",
            NormalizedUserName = $"TEST-{id:N}",
            Email = $"test-{id:N}@example.invalid",
            NormalizedEmail = $"TEST-{id:N}@EXAMPLE.INVALID",
            DisplayName = "Tenant isolation test user",
            Status = "ACTIVE",
            ForcePasswordChange = false,
            SessionVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            SecurityStamp = Guid.NewGuid().ToString("N")
        };
    }

    private static TenantMembership NewMembership(Guid tenantId, Guid userId, MembershipRole role) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        UserId = userId,
        Role = role,
        Status = RecordStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Version = 1
    };

    private static UserSession NewSession(Guid userId, Guid tenantId, string rawToken, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        TenantId = tenantId,
        State = SessionState.Active,
        TokenHash = new TokenHasher(Encoding.UTF8.GetBytes("tenant-isolation-test-key-32-bytes!!")).Hash(rawToken),
        SessionVersion = 1,
        IssuedAt = now,
        ReauthenticatedAt = now,
        LastSeenAt = now,
        ExpiresAt = now.AddMinutes(30),
        AbsoluteExpiresAt = now.AddHours(12)
    };

    private static OperationalIssue NewIssue(Guid tenantId, string key) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        DedupeKey = key,
        Code = "TEST",
        Summary = "Tenant isolation test",
        Status = IssueStatus.Open,
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
        OccurrenceCount = 1
    };

    private static IntegrationJob NewJob(Guid tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        return new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            JobType = "TEST_LEASE",
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "test-payload-hash",
            JobDedupKey = $"lease-{suffix}",
            EffectIdempotencyKey = $"effect-{suffix}",
            Priority = 1,
            Status = JobStatus.Pending,
            AvailableAt = now.AddMinutes(-1),
            MaxAttempts = 3,
            CorrelationId = $"corr-{suffix}",
            CreatedAt = now,
            Version = 1
        };
    }

    private static ApiIdempotencyRecord NewIdempotencyRecord(Guid tenantId, string route, string key) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        RouteTemplate = route,
        IdempotencyKey = key,
        RequestHash = "test-request-hash",
        State = "IN_PROGRESS",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private async Task DeleteJobAndTenantAsync(Guid jobId, Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteDeleteAsync();
        await db.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }

    private async Task DeleteIdempotencyRecordsAndTenantsAsync(Guid firstTenantId, Guid? secondTenantId = null)
    {
        await using var db = fixture.CreateContext();
        await db.ApiIdempotencyRecords
            .Where(x => x.TenantId == firstTenantId || (secondTenantId.HasValue && x.TenantId == secondTenantId.Value))
            .ExecuteDeleteAsync();
        await db.Tenants
            .Where(x => x.Id == firstTenantId || (secondTenantId.HasValue && x.Id == secondTenantId.Value))
            .ExecuteDeleteAsync();
    }

    private static DefaultHttpContext NewIdempotencyHttpContext(string path, string key, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.Headers["Idempotency-Key"] = key;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ComputeRequestHash(string method, string path, string query, string body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{method}\n{path}\n{query}\n");
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var combined = new byte[prefix.Length + bodyBytes.Length];
        prefix.CopyTo(combined, 0);
        bodyBytes.CopyTo(combined, prefix.Length);
        return Convert.ToHexString(SHA256.HashData(combined));
    }

    private sealed class FixedTenantContextAccessor(Guid tenantId) : ITenantContextAccessor
    {
        public TenantContext? Current { get; } = new(Guid.Empty, tenantId, "ADMIN");
    }
}

public sealed class PostgreSqlTenantIsolationFixture : IAsyncLifetime
{
    private readonly bool shouldMigrate = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION"));
    private readonly string databaseConnectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")
        ?? "Host=localhost;Port=5432;Database=marketplacehub;Username=marketplacehub;Password=development-only";

    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
    public TimeProvider TimeProvider { get; } = TimeProvider.System;
    public TokenHasher TokenHasher { get; } = new(Encoding.UTF8.GetBytes("tenant-isolation-test-key-32-bytes!!"));

    public async Task InitializeAsync()
    {
        if (!shouldMigrate) return;
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(databaseConnectionString).Options);

    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")))
            Skip = "PostgreSQL integration test requires MARKETPLACEHUB_TEST_CONNECTION pointing to a dedicated test database.";
    }
}
