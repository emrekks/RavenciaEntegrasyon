using System.Security.Claims;
using System.Text;
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
