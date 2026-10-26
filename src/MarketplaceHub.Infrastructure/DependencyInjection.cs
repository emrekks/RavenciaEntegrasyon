using System.Security.Cryptography.X509Certificates;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;
using MarketplaceHub.Infrastructure.Bootstrap;
using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketplaceHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMarketplaceInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = WithMaxPoolSize(WithoutJit(RequiredSecret(configuration, "ConnectionStrings:AppDb")), configuration["Database:MaxPoolSize"]);
        var credentialKey = Convert.FromBase64String(RequiredSecret(configuration, "Security:CredentialKey"));
        var filesRoot = configuration["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, "private-files");
        var keysRoot = configuration["DataProtection:KeysRoot"] ?? Path.Combine(AppContext.BaseDirectory, "dp-keys");

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connection, npgsql => npgsql.CommandTimeout(120)));
        services.AddMemoryCache(options => options.SizeLimit = 10_000);
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.Password.RequiredLength = 15; options.Password.RequiredUniqueChars = 4;
            options.Password.RequireDigit = true; options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true; options.Password.RequireNonAlphanumeric = true;
            // Account-wide ceiling for guesses spread over many networks, kept across restarts; one network is stopped
            // much earlier by the API's SignInThrottle (five failures). Reaching it does not lock the account for
            // everybody: the API still lets in signed-in sessions, networks the account recently signed in from and
            // networks that have not failed for it (PasswordCheck.IsBlockedAsync).
            options.Lockout.MaxFailedAccessAttempts = 30; options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.User.RequireUniqueEmail = true;
        }).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
        var dataProtection = services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysRoot)).SetApplicationName("MarketplaceHub");
        var certificatePath = configuration["DataProtection:CertificatePath"];
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            var certificatePassword = RequiredSecret(configuration, "DataProtection:CertificatePassword");
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            dataProtection.ProtectKeysWithCertificate(certificate);
        }
        else if (!string.Equals(configuration["MARKETPLACEHUB_ENVIRONMENT"], "PILOT_LOCAL", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A Data Protection key-encryption certificate is required outside PILOT_LOCAL.");
        }
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new TokenHasher(credentialKey));
        services.AddSingleton<TotpService>();
        services.AddScoped<IJobLeaseService, JobLeaseService>();
        services.AddScoped<JobRetentionService>();
        services.AddScoped<OperationalIssueRetentionService>();
        services.AddScoped<IJobOperationsService, JobOperationsService>();
        services.AddScoped<IDashboardReadService, DashboardReadService>();
        services.AddScoped<IReportsService, ReportsService>();
        services.AddScoped<IScheduledJobProducer, ScheduledJobProducer>();
        services.AddSingleton<CursorCodec>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IImportService, ImportService>();
        services.AddScoped<IImportJobProcessor, ImportJobProcessor>();
        services.AddScoped<IShopifyOrderCsvImportService, ShopifyOrderCsvImportService>();
        services.AddScoped<ICommissionTariffService, CommissionTariffService>();
        services.AddScoped<ICommissionTariffLookup, CommissionTariffLookup>();
        services.AddScoped<IPromotionOfferService, PromotionOfferService>();
        services.AddScoped<IProductTransferService, ProductTransferService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IReferenceDataService, ReferenceDataService>();
        services.Configure<TrendyolOptions>(configuration.GetSection(TrendyolOptions.SectionName));
        services.Configure<ShopifyOptions>(configuration.GetSection(ShopifyOptions.SectionName));
        services.Configure<HepsiburadaOptions>(configuration.GetSection(HepsiburadaOptions.SectionName));
        services.Configure<N11Options>(configuration.GetSection(N11Options.SectionName));
        services.Configure<PazaramaOptions>(configuration.GetSection(PazaramaOptions.SectionName));
        services.Configure<PttAvmOptions>(configuration.GetSection(PttAvmOptions.SectionName));
        services.AddSingleton<ISharedRateLimiter>(_ => new PostgresSharedRateLimiter(Npgsql.NpgsqlDataSource.Create(ForSharedRateLimiter(connection))));
        services.AddSingleton<TrendyolResilienceState>();
        services.AddTransient<TrendyolResilienceHandler>();
        services.AddHttpClient("Trendyol", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<TrendyolResilienceHandler>()
            .AddHttpMessageHandler(() => new MarketplaceHub.Infrastructure.Telemetry.MarketplaceRequestMetricsHandler("Trendyol"))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddScoped<TrendyolAuthenticationHandler>();
        services.AddScoped<TrendyolHttpClient>();
        services.AddSingleton<MarketplaceCircuitBreakerState>();
        services.AddTransient<MarketplaceCircuitBreakerHandler>();
        services.AddHttpClient("Shopify", client => client.Timeout = TimeSpan.FromSeconds(45))
            .AddHttpMessageHandler<MarketplaceCircuitBreakerHandler>()
            .AddHttpMessageHandler(() => new MarketplaceHub.Infrastructure.Telemetry.MarketplaceRequestMetricsHandler("Shopify"))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddScoped<ShopifyAuthenticationHandler>();
        services.AddScoped<ShopifyHttpClient>();
        services.AddScoped<ShopifyWebhookVerifier>();
        services.AddTransient<HepsiburadaRateLimitHandler>();
        services.AddHttpClient("Hepsiburada", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<MarketplaceCircuitBreakerHandler>()
            .AddHttpMessageHandler<HepsiburadaRateLimitHandler>()
            .AddHttpMessageHandler(() => new MarketplaceHub.Infrastructure.Telemetry.MarketplaceRequestMetricsHandler("Hepsiburada"))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddScoped<HepsiburadaAuthenticationHandler>();
        services.AddScoped<HepsiburadaHttpClient>();
        services.AddTransient<N11RateLimitHandler>();
        services.AddHttpClient("N11", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<MarketplaceCircuitBreakerHandler>()
            .AddHttpMessageHandler<N11RateLimitHandler>()
            .AddHttpMessageHandler(() => new MarketplaceHub.Infrastructure.Telemetry.MarketplaceRequestMetricsHandler("N11"))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddScoped<N11AuthenticationHandler>();
        services.AddScoped<N11HttpClient>();
        services.AddSingleton<PazaramaTokenCache>();
        services.AddTransient<PazaramaRateLimitHandler>();
        services.AddHttpClient(PazaramaHttpClient.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<MarketplaceCircuitBreakerHandler>()
            .AddHttpMessageHandler<PazaramaRateLimitHandler>()
            .AddHttpMessageHandler(() => new MarketplaceHub.Infrastructure.Telemetry.MarketplaceRequestMetricsHandler("Pazarama"))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddScoped<PazaramaAuthenticationHandler>();
        services.AddScoped<PazaramaHttpClient>();
        services.AddTransient<PttAvmRateLimitHandler>();
        services.AddHttpClient(PttAvmHttpClient.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<MarketplaceCircuitBreakerHandler>()
            .AddHttpMessageHandler<PttAvmRateLimitHandler>()
            .AddHttpMessageHandler(() => new MarketplaceHub.Infrastructure.Telemetry.MarketplaceRequestMetricsHandler("PttAvm"))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddScoped<PttAvmAuthenticationHandler>();
        services.AddScoped<PttAvmHttpClient>();
        services.AddScoped<MarketplacePortRouter>();
        services.AddScoped<IConnectionPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IReferenceDataPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IProductPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IHepsiburadaProductMatchPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IProductVisualLookupPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IReturnAuditPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IReturnAuditReader, ReturnAuditReader>();
        services.AddScoped<IInventoryPricePort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IOrderPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IOrderPackageReadPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IWebhookRegistrationPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IInstantNotificationService, InstantNotificationService>();
        services.AddScoped<IDataFreshnessService, DataFreshnessService>();
        services.AddScoped<IQueueHealthService, QueueHealthService>();
        services.AddScoped<ExternalEffectSweep>();
        services.AddScoped<IReturnPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IQuestionPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IHepsiburadaInvoiceStatusPort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<TrendyolWebhookVerifier>();
        services.AddScoped<HepsiburadaWebhookVerifier>();
        services.AddScoped<IWebhookVerifier, MarketplaceWebhookVerifier>();
        services.AddScoped<IMarketplaceConnectionService, MarketplaceConnectionService>();
        services.AddScoped<IOperationalDataMaintenanceService, OperationalDataMaintenanceService>();
        services.AddScoped<IMarketplaceSalesService, MarketplaceSalesService>();
        services.AddScoped<IMarketplaceQuestionService, MarketplaceQuestionService>();
        services.AddScoped<IMarketplaceWebhookService, MarketplaceWebhookService>();
        services.AddScoped<IJobHandler, MarketplaceHub.Infrastructure.Jobs.Handlers.QuestionSyncJobHandler>();
        services.AddScoped<ITrendyolFinancePort>(provider => provider.GetRequiredService<TrendyolHttpClient>());
        services.AddScoped<IJobHandler, MarketplaceHub.Infrastructure.Jobs.Handlers.TrendyolFinanceSyncJobHandler>();
        services.AddScoped<IJobHandler, MarketplaceHub.Infrastructure.Jobs.Handlers.PazaramaFinanceSyncJobHandler>();
        services.AddScoped<IMarketplaceJobProcessor, MarketplaceJobProcessor>();
        services.AddScoped<IMarketplaceReconciliationService, MarketplaceReconciliationService>();
        services.Configure<TrendyolEFaturamOptions>(configuration.GetSection(TrendyolEFaturamOptions.SectionName));
        services.AddHttpClient("TrendyolEFaturam", client => client.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddHttpClient("TrendyolEFaturamDocument", client => client.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = SafeRemoteDocumentDownloader.ConnectPublicOnlyAsync
        });
        services.AddScoped<SafeRemoteDocumentDownloader>();
        services.AddScoped<TrendyolEFaturamAuthenticationHandler>();
        services.AddSingleton<TrendyolEFaturamTokenCache>();
        services.AddScoped<TrendyolEFaturamHttpClient>();
        services.AddScoped<IInvoiceProviderPort>(provider => provider.GetRequiredService<TrendyolEFaturamHttpClient>());
        services.AddScoped<IInvoiceMarketplacePort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IOrderFinancePort>(provider => provider.GetRequiredService<MarketplacePortRouter>());
        services.AddScoped<IInvoicingBillingService, InvoicingBillingService>();
        services.AddScoped<IInvoicingJobProcessor, InvoicingJobProcessor>();
        services.AddScoped<IInvoiceInlineRunner, InvoiceInlineRunner>();
        services.AddScoped<IInvoicingReconciliationService, InvoicingReconciliationService>();
        services.AddSingleton<IPrivateFileStorage>(new PrivateFileStorage(filesRoot));
        services.Configure<BootstrapOptions>(configuration.GetSection("Bootstrap"));
        services.AddScoped<BootstrapService>();
        services.Configure<LocalDevelopmentSeedOptions>(configuration.GetSection("LocalDevelopmentSeed"));
        services.AddScoped<LocalDevelopmentSeedService>();
        return services;
    }

    public static void ApplyFileBackedSecrets(IConfigurationBuilder builder)
    {
        var snapshot = builder.Build();
        foreach (var pair in snapshot.AsEnumerable().Where(x => x.Key.EndsWith("_FILE", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Value)))
        {
            var path = Path.GetFullPath(pair.Value!);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > 65_536) throw new InvalidOperationException($"Secret file for {pair.Key} is missing or invalid.");
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [pair.Key[..^5]] = File.ReadAllText(path).Trim() });
        }
    }

    // PostgreSQL's JIT compiles a big query before running it. The panel's queries are small reads that finish in
    // milliseconds, but a wide one passes the JIT cost threshold: the 16-tab order summary took 12.5 s with JIT and 49 ms
    // without it (live, 21 Oct 2026). The session setting is sent with every connection; one already set is kept.
    internal static string WithoutJit(string connection)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);
        if (builder.Options?.Contains("jit", StringComparison.OrdinalIgnoreCase) == true) return connection;
        builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? "-c jit=off" : $"{builder.Options} -c jit=off";
        return builder.ConnectionString;
    }

    // The API and the Worker read the same connection file (Maximum Pool Size=25). The Worker needs a bigger pool
    // (WorkerLanes.RequiredPoolSize: two connections per lane slot plus its loops), so its appsettings.json sets
    // Database:MaxPoolSize, which replaces the file's value in that process only. Absent or invalid: unchanged.
    internal static string WithMaxPoolSize(string connection, string? maxPoolSize)
    {
        if (!int.TryParse(maxPoolSize, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) || size <= 0) return connection;
        return WithPoolLimit(connection, size);
    }

    // The shared rate limiter runs one short function call per request on its own NpgsqlDataSource, a pool separate
    // from EF's. A few connections are enough; uncapped it could take as many as the main pool from max_connections.
    internal const int SharedRateLimiterMaxPoolSize = 5;

    internal static string ForSharedRateLimiter(string connection) => WithPoolLimit(connection, SharedRateLimiterMaxPoolSize);

    private static string WithPoolLimit(string connection, int maxPoolSize)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);
        builder.MinPoolSize = Math.Min(builder.MinPoolSize, maxPoolSize);
        builder.MaxPoolSize = maxPoolSize;
        return builder.ConnectionString;
    }

    private static string RequiredSecret(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Required secret configuration '{key}' is missing.");
}
