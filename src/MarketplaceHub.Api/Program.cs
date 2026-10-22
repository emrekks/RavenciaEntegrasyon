using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Api.Dashboard;
using MarketplaceHub.Api.Invoicing;
using MarketplaceHub.Api.Marketplace;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Api.Realtime;
using MarketplaceHub.Api.Reports;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Api.Settings;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Bootstrap;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

if (args is ["healthcheck"])
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        Environment.ExitCode = 1;
    }
    catch (TaskCanceledException)
    {
        Environment.ExitCode = 1;
    }

    return;
}

var builder = WebApplication.CreateBuilder(args);
DependencyInjection.ApplyFileBackedSecrets(builder.Configuration);
builder.Host.UseSerilog((_, configuration) => configuration
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddMarketplaceInfrastructure(builder.Configuration);
builder.Services.AddScoped<BreakGlassService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContextAccessor, HttpTenantContextAccessor>();
builder.Services.AddProblemDetails();
// Unreadable bodies and parameters are thrown (also outside Development) so ApiExceptionHandler can
// answer them with 400 in the panel's error shape.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSignalR(options => options.AddFilter<HubOriginFilter>());
builder.Services.AddHostedService<OperationsRealtimeBroadcaster>();
builder.Services.AddSingleton<OperationsHubConnections>();
builder.Services.AddHostedService<OperationsHubSessionWatcher>();
builder.Services.AddHostedService<ApiIdempotencyRetentionService>();
builder.Services.AddHostedService<PurgedFileRetentionService>();
builder.Services.AddSingleton<SignInThrottle>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.30.10.0/24"));
});
builder.Services.AddSingleton<PostgresHealthCheck>();
builder.Services.AddSingleton<JobQueueHealthCheck>();
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<JobQueueHealthCheck>("job-queue", tags: ["ready", "queues"]);
builder.Services.AddHttpClient(PublicNetwork.ClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(12);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; RavenciaMediaProxy/1.0)");
}).ConfigurePrimaryHttpMessageHandler(PublicNetwork.Handler);
// Sign-in, the steps after it, a per-network ceiling and the webhook limits (see RateLimitPolicies).
builder.Services.AddRateLimiter(options => RateLimitPolicies.Configure(options, builder.Configuration));
builder.Services.AddScoped<IWebhookRouteGate, WebhookRouteGate>();

var app = builder.Build();
var isAdministrativeCommand = args.Any(argument => argument is "migrate" or "bootstrap" or "healthcheck");
if (!isAdministrativeCommand && builder.Environment.IsDevelopment() && string.Equals(builder.Configuration["MARKETPLACEHUB_ENVIRONMENT"], "PILOT_LOCAL", StringComparison.OrdinalIgnoreCase))
{
    await using var seedScope = app.Services.CreateAsyncScope();
    await seedScope.ServiceProvider.GetRequiredService<LocalDevelopmentSeedService>().EnsureAsync(CancellationToken.None);
}
// The migration session runs with its own lock_timeout and statement_timeout (DatabaseMigrator).
if (args is ["migrate"]) { await using var scope = app.Services.CreateAsyncScope(); await DatabaseMigrator.MigrateAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), app.Logger, CancellationToken.None); return; }
if (args is ["bootstrap"]) { await using var scope = app.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync(CancellationToken.None); return; }
if (args is ["identity", "reset-mfa", var email, var reason]) { await using var scope = app.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<BreakGlassService>().ResetMfaAsync(email, reason, CancellationToken.None); return; }
// Prints the one-time password once; the owner must change it at the next sign-in.
if (args is ["identity", "reset-password", var resetEmail, var resetReason])
{
    await using var scope = app.Services.CreateAsyncScope();
    var temporary = await scope.ServiceProvider.GetRequiredService<BreakGlassService>().ResetPasswordAsync(resetEmail, resetReason, CancellationToken.None);
    Console.Out.WriteLine($"Temporary password (change it at the next sign-in): {temporary}");
    return;
}

app.UseForwardedHeaders();
// Queue monitoring is for the server's own network (see FetchMetadata.IsFromServerNetwork). At the edge,
// Caddy answers every /health/* path except /health/live with 404 for public sources; the deploy scripts
// probe /health/ready from the host itself.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health/queues") && !FetchMetadata.IsFromServerNetwork(context))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var correlation = CorrelationId.FromRequest(context.Request.Headers["X-Correlation-ID"].FirstOrDefault());
    context.TraceIdentifier = correlation; context.Response.Headers["X-Correlation-ID"] = correlation;
    using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlation)) await next();
});
app.UseMiddleware<RequestSecurityMiddleware>();
// Before the session lookup and the idempotency body buffering: a refused request costs neither.
app.UseRateLimiter();
app.UseMiddleware<SessionAuthMiddleware>();
app.UseMiddleware<SessionStateBoundaryMiddleware>();
app.UseMiddleware<RoleAuthorizationMiddleware>();
app.UseMiddleware<ElevatedRoleMfaMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();
app.MapAuthEndpoints();
app.MapCatalogEndpoints();
app.MapDashboardEndpoints();
app.MapReportsEndpoints();
app.MapJobEndpoints();
app.MapMarketplaceEndpoints();
app.MapQuestionEndpoints();
app.MapInvoicingEndpoints();
app.MapSettingsEndpoints();
app.MapHub<OperationsHub>("/hubs/operations");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
// Monitoring endpoint: a stuck queue (Degraded) is reported as 503 here, while /health/ready stays 200.
app.MapHealthChecks("/health/queues", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("queues"),
    ResultStatusCodes =
    {
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
});
app.Run();

public partial class Program;
