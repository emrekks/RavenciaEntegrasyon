using MarketplaceHub.Infrastructure;
using MarketplaceHub.Worker;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);
DependencyInjection.ApplyFileBackedSecrets(builder.Configuration);
builder.Services.AddSerilog(configuration => configuration.WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddMarketplaceInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
