extern alias MarketplaceHubApi;
using System.Diagnostics;
using System.Net;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MarketplaceHub.EndToEnd.Tests;

using ApiProgram = MarketplaceHubApi::Program;

public sealed class FullStackFakeBrowserTests : IAsyncLifetime
{
    private PostgreSqlContainer? postgres;
    private string connectionString = string.Empty;
    private string? externalAdminConnection;
    private string? externalDatabase;
    private string? runtimeRoot;
    private WebApplicationFactory<ApiProgram>? factory;
    private Process? vite;
    private readonly Dictionary<string, string?> originalEnvironment = new(StringComparer.Ordinal);

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_POSTGRES") is { Length: > 0 } external)
        {
            externalAdminConnection = external;
            externalDatabase = $"marketplacehub_browser_e2e_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(external);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{externalDatabase}\"", connection);
            await command.ExecuteNonQueryAsync();
            connectionString = new NpgsqlConnectionStringBuilder(external) { Database = externalDatabase, Pooling = false }.ConnectionString;
        }
        else
        {
            postgres = new PostgreSqlBuilder("postgres:18.4@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
            await postgres.StartAsync();
            connectionString = postgres.GetConnectionString();
        }

        runtimeRoot = Path.Combine(Path.GetTempPath(), $"marketplacehub-full-stack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (vite is { HasExited: false }) vite.Kill(true);
        vite?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        if (postgres is not null) await postgres.DisposeAsync();
        if (externalAdminConnection is not null && externalDatabase is not null)
        {
            await using var connection = new NpgsqlConnection(externalAdminConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{externalDatabase}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
        if (runtimeRoot is not null && Directory.Exists(runtimeRoot)) Directory.Delete(runtimeRoot, true);
        foreach (var value in originalEnvironment) Environment.SetEnvironmentVariable(value.Key, value.Value);
    }

    [Fact]
    public async Task Browser_to_api_to_postgres_job_to_real_worker_to_fake_to_ui_is_complete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = FindRoot();
        var connectionId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var fake = new DeterministicFakeAdapter(FakeScenario.Success, TimeProvider.System);
        SetRuntimeEnvironment(new Dictionary<string, string?>
        {
            ["ConnectionStrings__AppDb"] = connectionString,
            ["Security__CredentialKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)29, 32).ToArray()),
            ["MARKETPLACEHUB_ENVIRONMENT"] = "PILOT_LOCAL",
            ["Storage__Root"] = Path.Combine(runtimeRoot!, "files"),
            ["DataProtection__KeysRoot"] = Path.Combine(runtimeRoot!, "keys")
        });
        factory = new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConnectionPort>();
                services.RemoveAll<IReferenceDataPort>();
                services.RemoveAll<IProductPort>();
                services.RemoveAll<IInventoryPricePort>();
                services.RemoveAll<IOrderPort>();
                services.RemoveAll<IReturnPort>();
                services.AddSingleton<IConnectionPort>(fake);
                services.AddSingleton<IReferenceDataPort>(fake);
                services.AddSingleton<IProductPort>(fake);
                services.AddSingleton<IInventoryPricePort>(fake);
                services.AddSingleton<IOrderPort>(fake);
                services.AddSingleton<IReturnPort>(fake);
                services.AddHostedService<MarketplaceHub.Worker.Worker>();
            });
        });
        factory.UseKestrel(0);
        using var api = factory.CreateClient(new() { AllowAutoRedirect = false });
        await SeedAsync(factory.Services, connectionId, cancellationToken);
        var apiAddress = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        const int uiPort = 5173;
        vite = StartVite(root, uiPort, apiAddress);
        var ui = new Uri($"http://127.0.0.1:{uiPort}");
        await WaitUntilReadyAsync(ui, vite, cancellationToken);

        var browser = StartBrowserProof(root, ui, connectionId);
        var outputTask = browser.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = browser.StandardError.ReadToEndAsync(cancellationToken);
        await browser.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.IntegrationJobs.SingleAsync(x => x.ConnectionId == connectionId && x.JobType == F3JobTypes.OrderSync, cancellationToken);
        var orderCount = await db.Orders.CountAsync(x => x.ConnectionId == connectionId, cancellationToken);
        Assert.True(browser.ExitCode == 0, $"Browser proof failed. job={job.Status} attempts={job.AttemptCount} error={job.LastErrorCode ?? "none"} orders={orderCount} stdout: {output} stderr: {error}");
        Assert.Contains("FULL_STACK_FAKE_E2E_PASS", output, StringComparison.Ordinal);
        Assert.Equal(1, orderCount);
        Assert.Equal(JobStatus.Succeeded, job.Status);
    }

    private static async Task SeedAsync(IServiceProvider services, Guid connectionId, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var user = new ApplicationUser { Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), UserName = "owner@fake.invalid", Email = "owner@fake.invalid", EmailConfirmed = true, DisplayName = "Fake E2E Owner", ForcePasswordChange = false, CreatedAt = now, UpdatedAt = now };
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var created = await users.CreateAsync(user, "Local-E2E-Only!9347");
        Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(x => x.Code)));
        db.Tenants.Add(new Tenant { Id = tenantId, Code = "browser-fake", DisplayName = "Browser Fake E2E", CreatedAt = now, UpdatedAt = now });
        db.TenantMemberships.Add(new TenantMembership { Id = Guid.NewGuid(), TenantId = tenantId, UserId = user.Id, Role = MembershipRole.Owner, CreatedAt = now, UpdatedAt = now });
        db.UserSecurities.Add(new UserSecurity { UserId = user.Id, TotpState = TotpState.Disabled });
        db.PlatformConnections.Add(new PlatformConnection { Id = connectionId, PublicId = Guid.NewGuid(), TenantId = tenantId, PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Deterministic Fake", ExternalStoreId = "synthetic-store", Status = "ACTIVE", ApiVersion = "V2", Version = 1 });
        db.PlatformCapabilities.Add(new PlatformCapability { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, Code = F3Capabilities.OrderRead, SupportLevel = CapabilitySupportLevel.Supported, ApiVersion = "test-v1", Environment = "TEST", StoreScope = "synthetic-store", SourceUrl = "test://deterministic-fake", SourceVersion = "test-v1", EvidenceNote = "Test-only browser release-candidate fixture.", VerifiedAt = now });
        db.FeatureFlags.Add(new FeatureFlag { Key = "external-writes", Enabled = false, UpdatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Process StartVite(string root, int port, Uri api)
    {
        var info = new ProcessStartInfo("node", $"node_modules/vite/bin/vite.js --host 127.0.0.1 --port {port} --strictPort") { WorkingDirectory = Path.Combine(root, "src", "MarketplaceHub.Web"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["VITE_API_PROXY"] = api.ToString().TrimEnd('/');
        return Process.Start(info) ?? throw new InvalidOperationException("Vite could not be started.");
    }

    private static Process StartBrowserProof(string root, Uri ui, Guid connectionId)
    {
        var info = new ProcessStartInfo("node", "e2e/full-stack-fake.mjs") { WorkingDirectory = Path.Combine(root, "src", "MarketplaceHub.Web"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["MARKETPLACEHUB_E2E_UI"] = ui.ToString().TrimEnd('/');
        info.Environment["MARKETPLACEHUB_E2E_CONNECTION_ID"] = connectionId.ToString("D");
        return Process.Start(info) ?? throw new InvalidOperationException("Browser proof could not be started.");
    }

    private static async Task WaitUntilReadyAsync(Uri ui, Process process, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (process.HasExited)
            {
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException($"Vite exited before readiness with code {process.ExitCode}. stdout: {output} stderr: {error}");
            }
            try { if ((await client.GetAsync(ui, cancellationToken)).StatusCode == HttpStatusCode.OK) return; } catch (HttpRequestException) { }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException("Vite did not become ready.");
    }

    private void SetRuntimeEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var value in values)
        {
            originalEnvironment[value.Key] = Environment.GetEnvironmentVariable(value.Key);
            Environment.SetEnvironmentVariable(value.Key, value.Value);
        }
    }

    private static string FindRoot()
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found");
        return path;
    }
}
