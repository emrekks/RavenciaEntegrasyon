using System.Security.Cryptography.X509Certificates;
using MarketplaceHub.Application;
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
        var connection = RequiredSecret(configuration, "ConnectionStrings:AppDb");
        var credentialKey = Convert.FromBase64String(RequiredSecret(configuration, "Security:CredentialKey"));
        var filesRoot = configuration["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, "private-files");
        var keysRoot = configuration["DataProtection:KeysRoot"] ?? Path.Combine(AppContext.BaseDirectory, "dp-keys");

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connection));
        services.AddMemoryCache(options => options.SizeLimit = 10_000);
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.Password.RequiredLength = 15; options.Password.RequiredUniqueChars = 4;
            options.Password.RequireDigit = true; options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true; options.Password.RequireNonAlphanumeric = true;
            options.Lockout.MaxFailedAccessAttempts = 5; options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
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
        services.AddScoped<IJobOperationsService, JobOperationsService>();
        services.AddScoped<IDashboardReadService, DashboardReadService>();
        services.AddScoped<IScheduledJobProducer, ScheduledJobProducer>();
        services.AddSingleton<CursorCodec>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IImportService, ImportService>();
        services.AddScoped<IImportJobProcessor, ImportJobProcessor>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IReferenceDataService, ReferenceDataService>();
        services.Configure<TrendyolOptions>(configuration.GetSection(TrendyolOptions.SectionName));
        services.AddSingleton<TrendyolResilienceState>();
        services.AddTransient<TrendyolResilienceHandler>();
        services.AddHttpClient("Trendyol", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<TrendyolResilienceHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddScoped<TrendyolAuthenticationHandler>();
        services.AddScoped<TrendyolHttpClient>();
        services.AddScoped<IConnectionPort>(provider => provider.GetRequiredService<TrendyolHttpClient>());
        services.AddScoped<IReferenceDataPort>(provider => provider.GetRequiredService<TrendyolHttpClient>());
        services.AddScoped<IProductPort>(provider => provider.GetRequiredService<TrendyolHttpClient>());
        services.AddScoped<IProductVisualLookupPort>(provider => provider.GetRequiredService<TrendyolHttpClient>());
        services.AddScoped<IInventoryPricePort>(provider => provider.GetRequiredService<TrendyolHttpClient>());
        services.AddScoped<IOrderPort>(provider => provider.GetRequiredService<TrendyolHttpClient>());
        services.AddScoped<IReturnPort>(provider => provider.GetRequiredService<TrendyolHttpClient>());
        services.AddScoped<IWebhookVerifier, TrendyolWebhookVerifier>();
        services.AddScoped<IMarketplaceConnectionService, MarketplaceConnectionService>();
        services.AddScoped<IOperationalDataMaintenanceService, OperationalDataMaintenanceService>();
        services.AddScoped<IMarketplaceSalesService, MarketplaceSalesService>();
        services.AddScoped<IMarketplaceWebhookService, MarketplaceWebhookService>();
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
        services.AddScoped<TrendyolEFaturamHttpClient>();
        services.AddScoped<IInvoiceProviderPort>(provider => provider.GetRequiredService<TrendyolEFaturamHttpClient>());
        services.AddScoped<IInvoiceMarketplacePort>(provider => provider.GetRequiredService<TrendyolHttpClient>());
        services.AddScoped<IInvoicingBillingService, InvoicingBillingService>();
        services.AddScoped<IInvoicingJobProcessor, InvoicingJobProcessor>();
        services.AddScoped<IInvoicingReconciliationService, InvoicingReconciliationService>();
        services.AddSingleton<IPrivateFileStorage>(new PrivateFileStorage(filesRoot));
        services.Configure<BootstrapOptions>(configuration.GetSection("Bootstrap"));
        services.AddScoped<BootstrapService>();
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

    private static string RequiredSecret(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Required secret configuration '{key}' is missing.");
}
