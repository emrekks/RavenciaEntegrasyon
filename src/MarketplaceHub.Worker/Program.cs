using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Worker;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);
DependencyInjection.ApplyFileBackedSecrets(builder.Configuration);
builder.Services.AddSerilog(configuration => configuration.WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddMarketplaceInfrastructure(builder.Configuration);
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
