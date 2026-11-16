using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-7: a GET that calls a marketplace live must declare it (.LiveMarketplaceRead(): Working gate, same-origin
// only). ApiEndpointRulesTests holds the declarations to the path rule; this test finds a live call the path rule does
// not know about. Every marketplace port is a fake that counts its calls and fails; a signed-in owner calls every GET
// of the API once per seeded store (Trendyol, Hepsiburada, N11, Pazarama: each with an order and a package), and every
// GET that moved the counter must declare itself a live read. What it cannot see: a read that needs a record of a
// kind not seeded here (a return, a question, a product), or one that calls a marketplace client without a port.
[Collection("postgres-integration")]
public sealed class ApiLiveReadPortCountTests(LivePortCountingFixture fixture) : IClassFixture<LivePortCountingFixture>
{
    // Live reads that must be reached by this run, so a change that stops the probe reaching ports fails here instead of
    // passing on nothing.
    private static readonly string[] MustReach =
    [
        "/api/v1/orders/{id:guid}/finance",
        "/api/v1/orders/product-image",
        "/api/v1/shipments/{id:guid}/changeable-cargo-companies",
    ];

    [PostgreSqlFact]
    public async Task EveryGetThatCallsAMarketplaceDeclaresItselfALiveRead()
    {
        var host = fixture.Host!;
        var gets = ApiRouteTable.Routes(host).Where(route => route.Method == "GET" && route.Pattern.StartsWith("/api/v1/", StringComparison.Ordinal)
            && !route.Pattern.StartsWith("/api/v1/hooks/", StringComparison.Ordinal) && !route.Pattern.StartsWith("/api/v1/public/", StringComparison.Ordinal)).ToList();
        var (client, _) = await fixture.SignInOwnerAsync();
        using (client)
        {
            var reached = new Dictionary<string, RouteEndpointInfo>(StringComparer.Ordinal);
            foreach (var store in fixture.Stores)
            {
                foreach (var (_, pattern, endpoint) in gets)
                {
                    var path = Fill(pattern, store);
                    var before = CountingPort.Calls;
                    using var request = new HttpRequestMessage(HttpMethod.Get, path);
                    request.Headers.Add("Sec-Fetch-Site", "same-origin");
                    using var response = await client.SendAsync(request);
                    if (CountingPort.Calls != before) reached[pattern] = new(pattern, endpoint.Metadata.GetMetadata<LiveMarketplaceReadMetadata>() is not null, CountingPort.LastMethod);
                }
            }
            var undeclared = reached.Values.Where(route => !route.Declared).Select(route => $"{route.Pattern} (called {route.Port})").Order(StringComparer.Ordinal).ToList();
            Assert.True(undeclared.Count == 0, "GETs that called a marketplace port without .LiveMarketplaceRead():\n" + string.Join("\n", undeclared));
            Assert.All(MustReach, pattern => Assert.True(reached.ContainsKey(pattern), $"{pattern} no longer reached a port; reached: {string.Join(", ", reached.Keys)}"));
        }
    }

    private sealed record RouteEndpointInfo(string Pattern, bool Declared, string? Port);

    // Route values from the store's records by the segment before them; a query every read may ignore names the store.
    private static string Fill(string pattern, LivePortCountingFixture.Store store)
    {
        var path = Regex.Replace(Regex.Replace(pattern, @"\{\*\*[^}]+\}", "event/path"), @"(?<=/(?<segment>[^/{]+)/)\{(?<name>[^}:]+)(?<guid>:guid)?\}|\{(?<name>[^}:]+)(?<guid>:guid)?\}", match =>
        {
            var name = match.Groups["name"].Value;
            var segment = match.Groups["segment"].Value;
            if (!match.Groups["guid"].Success) return "x";
            if (name == "connectionId" || segment == "connections") return store.ConnectionId.ToString();
            if (name == "orderId" || segment == "orders") return store.OrderId.ToString();
            if (name == "packageId" || segment == "shipments") return store.PackageId.ToString();
            return Guid.CreateVersion7().ToString();
        });
        return $"{path}?connectionId={store.ConnectionId}&platform={store.PlatformCode}&barcode=8690000000011&code=X&q=x&externalId=1";
    }
}

/// <summary>Counts the calls of every faked marketplace port (one static counter: the test runs its requests one by one).</summary>
public class CountingPort : DispatchProxy
{
    private static int calls;
    public static int Calls => Volatile.Read(ref calls);
    public static string? LastMethod { get; private set; }

    // A call that returns a task goes over the network; a synchronous member is a fixed capability table
    // (IQuestionPort.ReportCapability) and is not a live call, so it fails without counting.
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = $"{targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}";
        if (targetMethod is not null && typeof(Task).IsAssignableFrom(targetMethod.ReturnType))
        {
            Interlocked.Increment(ref calls);
            LastMethod = name;
        }
        throw new InvalidOperationException($"Fake marketplace port: {name}");
    }

    public static object Create(Type port) =>
        typeof(DispatchProxy).GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)!.MakeGenericMethod(port, typeof(CountingPort)).Invoke(null, null)!;
}

public sealed class LivePortCountingFixture : IAsyncLifetime
{
    public sealed record Store(string PlatformCode, Guid ConnectionId, Guid OrderId, Guid PackageId);

    private const string Password = "Ravencia-Test-Pass#2026";
    private readonly string? connectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION");
    private readonly string run = Guid.NewGuid().ToString("N")[..12];
    private readonly Guid tenantId = Guid.CreateVersion7();

    public ApiTestHost? Host { get; private set; }
    public List<Store> Stores { get; } = [];
    private string Email => $"owner-live-{run}@example.invalid";

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using (var migrate = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options))
            await migrate.Database.MigrateAsync();
        Host = new CountingPortHost(connectionString);
        await using var scope = Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant { Id = tenantId, Code = $"live-{run}", DisplayName = $"Live {run}", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = Email, Email = Email, DisplayName = "Live Owner", ForcePasswordChange = false, CreatedAt = now, UpdatedAt = now };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        db.UserSecurities.Add(new UserSecurity { UserId = user.Id, TotpState = TotpState.Disabled });
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = tenantId, UserId = user.Id, Role = MembershipRole.Owner, CreatedAt = now, UpdatedAt = now });
        foreach (var platform in new[] { "TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA" })
        {
            var store = new Store(platform, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            var tag = $"{platform.ToLowerInvariant()}-{run}";
            db.PlatformConnections.Add(new PlatformConnection { Id = store.ConnectionId, TenantId = tenantId, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = $"{platform} {run}", ExternalStoreId = $"store-{tag}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 });
            db.Orders.Add(new Order { Id = store.OrderId, TenantId = tenantId, ConnectionId = store.ConnectionId, ExternalOrderId = $"o-{tag}", OrderNumber = $"o-{tag}", Currency = "TRY", GrossAmount = 100m, NetAmount = 100m, OrderedAt = now.AddDays(-2), LastRemoteModifiedAt = now.AddDays(-2), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "NEW", CreatedAt = now.AddDays(-2), UpdatedAt = now, Version = 1 });
            db.ShipmentPackages.Add(new ShipmentPackage { Id = store.PackageId, TenantId = tenantId, ConnectionId = store.ConnectionId, OrderId = store.OrderId, ExternalPackageId = $"p-{tag}", GrossAmount = 100m, NetAmount = 100m, Status = ShipmentPackageStatus.New, RawStatus = "open", StatusOccurredAt = now.AddDays(-2), MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown, CreatedAt = now.AddDays(-2), UpdatedAt = now, Version = 1 });
            Stores.Add(store);
        }
        await db.SaveChangesAsync();
    }

    public async Task<(HttpClient Client, string Csrf)> SignInOwnerAsync()
    {
        var client = Host!.ClientFromOwnNetwork();
        var csrf = await Host.CsrfAsync(client);
        using var signIn = await client.SendAsync(ApiTestHost.Json(HttpMethod.Post, "/api/v1/auth/login", csrf, new { email = Email, password = Password }));
        signIn.EnsureSuccessStatusCode();
        var token = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
        return (client, csrf);
    }

    public Task DisposeAsync()
    {
        Host?.Dispose();
        return Task.CompletedTask;
    }

    // The API with every marketplace port replaced by a counting fake: the ports MarketplacePortRouter serves and the
    // finance ports the adapters' clients serve.
    private sealed class CountingPortHost(string connectionString) : ApiTestHost(connectionString)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var ports = typeof(MarketplacePortRouter).GetInterfaces().Where(type => type.Name.EndsWith("Port", StringComparison.Ordinal))
                    .Concat([typeof(ITrendyolFinancePort), typeof(IHepsiburadaFinancePort), typeof(IN11SettlementPort)]).Distinct().ToList();
                foreach (var port in ports)
                {
                    foreach (var registered in services.Where(descriptor => descriptor.ServiceType == port).ToList()) services.Remove(registered);
                    services.AddScoped(port, _ => CountingPort.Create(port));
                }
            });
        }
    }
}
