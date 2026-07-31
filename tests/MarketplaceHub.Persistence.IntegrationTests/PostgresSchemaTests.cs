using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Bootstrap;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MarketplaceHub.Persistence.IntegrationTests;

public sealed class PostgresSchemaTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string _connectionString = string.Empty;
    private string? _externalAdminConnection;
    private string? _externalDatabase;

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_POSTGRES") is { Length: > 0 } external)
        {
            _externalAdminConnection = external;
            _externalDatabase = $"marketplacehub_test_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(external); await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_externalDatabase}\"", connection); await command.ExecuteNonQueryAsync();
            _connectionString = new NpgsqlConnectionStringBuilder(external) { Database = _externalDatabase, Pooling = false }.ConnectionString;
            return;
        }
        _postgres = new PostgreSqlBuilder("postgres:18.4@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await _postgres.StartAsync(); _connectionString = _postgres.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null) { await _postgres.DisposeAsync(); return; }
        if (_externalAdminConnection is null || _externalDatabase is null) return;
        await using var connection = new NpgsqlConnection(_externalAdminConnection); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_externalDatabase}\" WITH (FORCE)", connection); await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Migration_creates_the_F1_F2_and_F3_schemas_without_bootstrap_data()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
        Assert.Empty(await db.BootstrapStates.ToListAsync(cancellationToken));
        Assert.Empty(await db.Users.ToListAsync(cancellationToken));
        Assert.Empty(await db.Tenants.ToListAsync(cancellationToken));
        Assert.Empty(await db.Orders.ToListAsync(cancellationToken)); Assert.Empty(await db.PlatformCredentials.ToListAsync(cancellationToken));
        var schemas = await db.Database.SqlQueryRaw<string>("SELECT schema_name AS \"Value\" FROM information_schema.schemata WHERE schema_name IN ('iam','ops','integration','catalog','inventory','sales') ORDER BY schema_name").ToListAsync(cancellationToken);
        Assert.Equal(new[] { "catalog", "iam", "integration", "inventory", "ops", "sales" }, schemas);
    }

    [Fact]
    public async Task Concurrent_and_repeated_bootstrap_creates_exactly_one_owner_graph()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options))
            await db.Database.MigrateAsync(cancellationToken);
        var password = $"Aa!9-{Guid.NewGuid():N}";
        await using var providerOne = BuildProvider(password);
        await using var providerTwo = BuildProvider(password);
        await using var scopeOne = providerOne.CreateAsyncScope();
        await using var scopeTwo = providerTwo.CreateAsyncScope();
        await Task.WhenAll(
            scopeOne.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync(cancellationToken),
            scopeTwo.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync(cancellationToken));
        await scopeOne.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync(cancellationToken);

        await using var verification = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);
        Assert.Equal(1, await verification.BootstrapStates.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.Tenants.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.Users.CountAsync(cancellationToken));
        Assert.Equal(1, await verification.TenantMemberships.CountAsync(cancellationToken));
        Assert.True((await verification.Users.SingleAsync(cancellationToken)).ForcePasswordChange);
    }

    private ServiceProvider BuildProvider(string password)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AppDb"] = _connectionString,
            ["MARKETPLACEHUB_ENVIRONMENT"] = "PILOT_LOCAL",
            ["Security:CredentialKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray()),
            ["Bootstrap:Enabled"] = "true",
            ["Bootstrap:TenantCode"] = "ravencia",
            ["Bootstrap:TenantDisplayName"] = "Ravencia",
            ["Bootstrap:OwnerEmail"] = "owner@example.invalid",
            ["Bootstrap:OwnerDisplayName"] = "Ravencia Admin",
            ["Bootstrap:OwnerPassword"] = password,
            ["Storage:Root"] = Path.Combine(Path.GetTempPath(), "marketplacehub-test-files"),
            ["DataProtection:KeysRoot"] = Path.Combine(Path.GetTempPath(), "marketplacehub-test-keys")
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(configuration); services.AddMarketplaceInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Job_dedup_lease_heartbeat_and_stale_token_guards_hold()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
        var tenant = new Tenant { Id = Guid.NewGuid(), Code = $"job-{Guid.NewGuid():N}", DisplayName = "Job Test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Tenants.Add(tenant);
        db.IntegrationJobs.Add(NewJob(tenant.Id));
        await db.SaveChangesAsync(cancellationToken);
        await using (var duplicateDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options))
        {
            duplicateDb.IntegrationJobs.Add(NewJob(tenant.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateDb.SaveChangesAsync(cancellationToken));
        }
        var hasher = new TokenHasher(Enumerable.Repeat((byte)11, 32).ToArray());
        var leases = new JobLeaseService(db, hasher, TimeProvider.System);
        var lease = Assert.IsType<MarketplaceHub.Application.LeasedJob>(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), cancellationToken));
        Assert.False(await leases.HeartbeatAsync(lease.Id, "stale-token", TimeSpan.FromMinutes(1), cancellationToken));
        Assert.True(await leases.HeartbeatAsync(lease.Id, lease.LeaseToken, TimeSpan.FromMinutes(1), cancellationToken));
        Assert.True(await leases.CompleteAsync(lease.Id, lease.LeaseToken, true, null, cancellationToken));
        Assert.False(await leases.CompleteAsync(lease.Id, lease.LeaseToken, true, null, cancellationToken));

        db.IntegrationJobs.Add(NewJob(tenant.Id, "worker-kill")); await db.SaveChangesAsync(cancellationToken);
        var abandoned = Assert.IsType<MarketplaceHub.Application.LeasedJob>(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), cancellationToken));
        var abandonedEntity = await db.IntegrationJobs.SingleAsync(x => x.Id == abandoned.Id, cancellationToken);
        abandonedEntity.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync(cancellationToken);
        Assert.Equal(1, await leases.ReapExpiredAsync(cancellationToken));
        await db.Entry(abandonedEntity).ReloadAsync(cancellationToken);
        Assert.Equal(JobStatus.Pending, abandonedEntity.Status);
    }

    [Fact]
    public async Task Parallel_recovery_code_consumption_has_one_winner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var setup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);
        await setup.Database.MigrateAsync(cancellationToken);
        var userId = Guid.NewGuid(); var digest = Guid.NewGuid().ToString("N");
        setup.Users.Add(new ApplicationUser { Id = userId, UserName = $"recovery-{userId:N}", NormalizedUserName = $"RECOVERY-{userId:N}", Email = $"recovery-{userId:N}@example.invalid", NormalizedEmail = $"RECOVERY-{userId:N}@EXAMPLE.INVALID", DisplayName = "Recovery Test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        setup.RecoveryCodes.Add(new RecoveryCode { Id = Guid.NewGuid(), UserId = userId, BatchId = Guid.NewGuid(), CodeDigest = digest, CreatedAt = DateTimeOffset.UtcNow });
        await setup.SaveChangesAsync(cancellationToken);
        async Task<int> ConsumeAsync()
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);
            return await db.RecoveryCodes.Where(x => x.UserId == userId && x.CodeDigest == digest && x.UsedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.UsedAt, DateTimeOffset.UtcNow), cancellationToken);
        }
        var results = await Task.WhenAll(ConsumeAsync(), ConsumeAsync());
        Assert.Equal(1, results.Sum());
    }

    [Fact]
    public async Task Csv_import_preview_review_apply_and_repeat_do_not_duplicate_the_core()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var setup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options))
        {
            await setup.Database.MigrateAsync(cancellationToken);
            setup.Tenants.Add(new Tenant { Id = ImportTenantId, Code = $"import-{Guid.NewGuid():N}", DisplayName = "Import Test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync(cancellationToken);
        }
        await using var provider = BuildProvider($"Aa!9-{Guid.NewGuid():N}");
        await using var scope = provider.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportService>();
        var processor = scope.ServiceProvider.GetRequiredService<IImportJobProcessor>();

        var first = await PrepareCsvAsync(imports, cancellationToken);
        Assert.True((await imports.EnqueuePreviewAsync(ImportTenantId, first.Id, "test-preview-1", cancellationToken)).Succeeded);
        Assert.True(await processor.ProcessAsync(ImportTenantId, first.Id, "PREVIEW", cancellationToken));
        var firstCandidate = Assert.Single((await imports.CandidatesAsync(ImportTenantId, first.Id, 50, null, cancellationToken)).Items);
        Assert.Equal("NEW", firstCandidate.MatchRule);
        Assert.True((await imports.DecideAsync(ImportTenantId, Guid.NewGuid(), first.Id, firstCandidate.Id, firstCandidate.Version, new("CREATE", null, null), cancellationToken)).Succeeded);
        Assert.True((await imports.EnqueueApplyAsync(ImportTenantId, first.Id, "test-apply-1", cancellationToken)).Succeeded);
        Assert.True(await processor.ProcessAsync(ImportTenantId, first.Id, "APPLY", cancellationToken));

        await using (var verify = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options))
        {
            Assert.Equal(1, await verify.Products.CountAsync(x => x.TenantId == ImportTenantId, cancellationToken));
            Assert.Equal(1, await verify.ProductVariants.CountAsync(x => x.TenantId == ImportTenantId, cancellationToken));
            Assert.Equal(1, await verify.InventoryItems.CountAsync(x => x.TenantId == ImportTenantId && x.LocationCode == "MAIN", cancellationToken));
        }

        var second = await PrepareCsvAsync(imports, cancellationToken);
        Assert.True((await imports.EnqueuePreviewAsync(ImportTenantId, second.Id, "test-preview-2", cancellationToken)).Succeeded);
        Assert.True(await processor.ProcessAsync(ImportTenantId, second.Id, "PREVIEW", cancellationToken));
        var secondCandidate = Assert.Single((await imports.CandidatesAsync(ImportTenantId, second.Id, 50, null, cancellationToken)).Items);
        Assert.Equal("UNIQUE_BARCODE", secondCandidate.MatchRule);
        Assert.NotNull(secondCandidate.ProductId); Assert.NotNull(secondCandidate.VariantId);
        Assert.True((await imports.DecideAsync(ImportTenantId, Guid.NewGuid(), second.Id, secondCandidate.Id, secondCandidate.Version, new("LINK", secondCandidate.ProductId, secondCandidate.VariantId), cancellationToken)).Succeeded);
        Assert.True((await imports.EnqueueApplyAsync(ImportTenantId, second.Id, "test-apply-2", cancellationToken)).Succeeded);
        Assert.True(await processor.ProcessAsync(ImportTenantId, second.Id, "APPLY", cancellationToken));
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Products.CountAsync(x => x.TenantId == ImportTenantId, cancellationToken));
        Assert.Equal(1, await db.ProductVariants.CountAsync(x => x.TenantId == ImportTenantId, cancellationToken));
    }

    private async Task<ImportSessionView> PrepareCsvAsync(IImportService imports, CancellationToken cancellationToken)
    {
        var created = (await imports.CreateAsync(ImportTenantId, new("CSV", null), cancellationToken)).Value!;
        var bytes = System.Text.Encoding.UTF8.GetBytes("Ürün,SKU,Barkod,Stok\r\nGömlek,SKU-001,869000000001,12\r\n");
        var attached = (await imports.AttachSourceAsync(ImportTenantId, created.Id, new("products.csv", "text/csv", new MemoryStream(bytes), bytes.Length), cancellationToken)).Value!;
        return (await imports.ConfigureColumnsAsync(ImportTenantId, created.Id, attached.Version, new("Test", null, [new("Ürün", "title", 0), new("SKU", "sku", 1), new("Barkod", "barcode", 2), new("Stok", "stock", 3)]), cancellationToken)).Value!;
    }

    private static readonly Guid ImportTenantId = Guid.Parse("d3c3c314-25cf-4e80-b813-71d4ec9ab037");

    [Fact]
    public async Task Ledger_idempotency_price_history_and_unknown_capability_guards_hold()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var setup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options))
        {
            await setup.Database.MigrateAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            setup.Tenants.Add(new Tenant { Id = GuardTenantId, Code = $"guard-{Guid.NewGuid():N}", DisplayName = "Guard Test", CreatedAt = now, UpdatedAt = now });
            setup.Products.Add(new Product { Id = GuardProductId, TenantId = GuardTenantId, Title = "Test", Description = "", CreatedAt = now, UpdatedAt = now });
            setup.ProductVariants.Add(new ProductVariant { Id = GuardVariantId, TenantId = GuardTenantId, ProductId = GuardProductId, Sku = "GUARD-1", SkuNormalized = "GUARD-1", OptionSignature = "DEFAULT", CreatedAt = now, UpdatedAt = now });
            setup.InventoryItems.Add(new InventoryItem { Id = GuardInventoryId, TenantId = GuardTenantId, VariantId = GuardVariantId, LocationCode = "MAIN", OnHand = 0, Reserved = 0, Available = 0 });
            setup.ChannelOffers.Add(new ChannelOffer { Id = GuardOfferId, TenantId = GuardTenantId, ConnectionId = Guid.NewGuid(), VariantId = GuardVariantId, ListPrice = 0, SalePrice = 0, Currency = "TRY", VatRate = 0, VatInclusion = "FIXTURE", RoundingMode = "FIXTURE", SafetyStock = 0, Status = "DRAFT" });
            await setup.SaveChangesAsync(cancellationToken);
        }
        await using var provider = BuildProvider($"Aa!9-{Guid.NewGuid():N}"); await using var scope = provider.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
        var first = await inventory.AdjustAsync(GuardTenantId, Guid.NewGuid(), GuardVariantId, new(5, "fixture", "event-1"), "same-ledger-key", "corr-1", cancellationToken);
        var replay = await inventory.AdjustAsync(GuardTenantId, Guid.NewGuid(), GuardVariantId, new(5, "fixture", "event-1"), "same-ledger-key", "corr-2", cancellationToken);
        Assert.True(first.Succeeded); Assert.True(replay.Succeeded); Assert.Equal(5, replay.Value!.OnHand);
        var updated = await inventory.UpdateOfferAsync(GuardTenantId, Guid.NewGuid(), GuardOfferId, 1, new(100, 90, "TRY", 20, "FIXTURE", "FIXTURE", 2, "DRAFT", "fixture"), cancellationToken);
        Assert.True(updated.Succeeded); Assert.Equal(2, updated.Value!.PriceVersion);
        Assert.False((await inventory.ValidateExternalSyncAsync(GuardTenantId, "STOCK_SYNC", cancellationToken)).Succeeded);
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogService>(); Assert.False((await catalog.ValidatePublicationAsync(GuardTenantId, GuardProductId, Guid.NewGuid(), cancellationToken)).Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); db.ChangeTracker.Clear();
        Assert.Equal(1, await db.StockLedgerEntries.CountAsync(x => x.TenantId == GuardTenantId, cancellationToken));
        Assert.Equal(1, await db.ChannelPriceHistory.CountAsync(x => x.TenantId == GuardTenantId && x.OfferId == GuardOfferId, cancellationToken));
        Assert.Equal(0, await db.IntegrationJobs.CountAsync(x => x.TenantId == GuardTenantId, cancellationToken));
    }

    private static readonly Guid GuardTenantId = Guid.Parse("c8b4fd8a-b6a0-4dc3-8fb2-34db2c8e125c");
    private static readonly Guid GuardProductId = Guid.Parse("0c956e7d-fdf9-41b0-8d3b-c66c67f04b8a");
    private static readonly Guid GuardVariantId = Guid.Parse("6bbb7de1-98d1-44dd-84f7-936dcf80f85c");
    private static readonly Guid GuardInventoryId = Guid.Parse("b49c16da-2d22-42da-9e22-4a63e629a47d");
    private static readonly Guid GuardOfferId = Guid.Parse("556590ca-e8ba-4db6-9d28-06b5a86752e4");

    [Fact]
    public async Task One_thousand_product_catalog_is_traversed_with_bounded_signed_cursor_pages()
    {
        var cancellationToken = TestContext.Current.CancellationToken; var tenantId = Guid.NewGuid();
        await using (var setup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options))
        {
            await setup.Database.MigrateAsync(cancellationToken); var now = DateTimeOffset.UtcNow;
            setup.Tenants.Add(new Tenant { Id = tenantId, Code = $"page-{tenantId:N}", DisplayName = "Paging Test", CreatedAt = now, UpdatedAt = now });
            setup.Products.AddRange(Enumerable.Range(0, 1_000).Select(index => new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = $"Ürün {index:D4}", Description = "", CreatedAt = now, UpdatedAt = now }));
            await setup.SaveChangesAsync(cancellationToken);
        }
        await using var provider = BuildProvider($"Aa!9-{Guid.NewGuid():N}"); await using var scope = provider.CreateAsyncScope(); var catalog = scope.ServiceProvider.GetRequiredService<ICatalogService>();
        string? cursor = null; var seen = new HashSet<Guid>(); var pages = 0; var durations = new List<double>();
        do
        {
            var timer = System.Diagnostics.Stopwatch.StartNew(); var page = await catalog.ListProductsAsync(tenantId, 50, cursor, null, cancellationToken); timer.Stop(); durations.Add(timer.Elapsed.TotalMilliseconds); pages++; foreach (var item in page.Items) Assert.True(seen.Add(item.Id)); cursor = page.NextCursor;
        } while (cursor is not null);
        var p95 = durations.Order().ElementAt((int)Math.Ceiling(durations.Count * 0.95) - 1);
        TestContext.Current.TestOutputHelper?.WriteLine($"Product list p95: {p95:F2} ms");
        Assert.Equal(1_000, seen.Count); Assert.Equal(20, pages); Assert.True(p95 < 2_000, $"Product list p95 was {p95:F2} ms.");
    }

    private static IntegrationJob NewJob(Guid tenantId, string dedup = "same") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        JobType = "F1_GUARD_TEST",
        PayloadJson = "{}",
        PayloadVersion = 1,
        PayloadHash = "hash",
        JobDedupKey = dedup,
        EffectIdempotencyKey = $"effect-{dedup}",
        AvailableAt = DateTimeOffset.UtcNow,
        CorrelationId = Guid.NewGuid().ToString("N")
    };
}
