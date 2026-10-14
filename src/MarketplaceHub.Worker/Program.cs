using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Worker;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);
AddWorkerAppSettings(builder);
DependencyInjection.ApplyFileBackedSecrets(builder.Configuration);
builder.Services.AddSerilog(configuration => configuration.WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddMarketplaceInfrastructure(builder.Configuration);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(40));
builder.Services.AddHostedService<Worker>();
var host = builder.Build();

if (args is ["efaturam-connection-test", var publicIdText]
    && Guid.TryParse(publicIdText, out var publicId))
{
    await using var scope = host.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(
        value => value.PublicId == publicId && value.PlatformCode == "TRENDYOL_EFATURAM");
    if (connection is null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { success = false, code = "CONNECTION_NOT_FOUND" }));
        return;
    }

    var provider = scope.ServiceProvider.GetRequiredService<IInvoiceProviderPort>();
    var result = await provider.TestConnectionAsync(new(
        connection.TenantId,
        connection.Id,
        Guid.NewGuid().ToString("N"),
        $"diagnostic-{Guid.NewGuid():N}",
        DateTimeOffset.UtcNow.AddMinutes(1),
        Operation: IntegrationOperation.Manual), CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        success = result.IsSuccess,
        code = result.Error?.Code,
        status = result.Error?.HttpStatus,
        remoteReference = result.Error?.RemoteRequestId
    }));
    return;
}

await host.RunAsync();

// In containers the content root is /app (shared with the API) while the worker binaries,
// including its own appsettings.json, live in /app/worker. Load that file right after the
// content-root JSON files so worker settings apply but environment variables still win.
static void AddWorkerAppSettings(HostApplicationBuilder builder)
{
    var workerDirectory = AppContext.BaseDirectory;
    var contentRoot = Path.GetFullPath(builder.Environment.ContentRootPath);
    if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workerDirectory)), Path.TrimEndingDirectorySeparator(contentRoot), StringComparison.OrdinalIgnoreCase)) return;
    var sources = builder.Configuration.Sources;
    var insertAt = 0;
    for (var index = 0; index < sources.Count; index++)
        if (sources[index] is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource) insertAt = index + 1;
    var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(workerDirectory);
    foreach (var fileName in new[] { "appsettings.json", $"appsettings.{builder.Environment.EnvironmentName}.json" })
    {
        if (!File.Exists(Path.Combine(workerDirectory, fileName))) continue;
        sources.Insert(insertAt++, new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource { FileProvider = fileProvider, Path = fileName, Optional = true, ReloadOnChange = false });
    }
}
