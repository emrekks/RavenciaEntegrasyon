using System.Threading.RateLimiting;
using MarketplaceHub.Api.F2;
using MarketplaceHub.Api.F3;
using MarketplaceHub.Api.F4;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure;
using MarketplaceHub.Infrastructure.Bootstrap;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
DependencyInjection.ApplyFileBackedSecrets(builder.Configuration);
builder.Host.UseSerilog((_, configuration) => configuration.WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddMarketplaceInfrastructure(builder.Configuration);
builder.Services.AddScoped<BreakGlassService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContextAccessor, HttpTenantContextAccessor>();
builder.Services.AddProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.30.10.0/24"));
});
builder.Services.AddSingleton<PostgresHealthCheck>();
builder.Services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
if (args is ["migrate"]) { await using var scope = app.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(); return; }
if (args is ["bootstrap"]) { await using var scope = app.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync(CancellationToken.None); return; }
if (args is ["identity", "reset-mfa", var email, var reason]) { await using var scope = app.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<BreakGlassService>().ResetMfaAsync(email, reason, CancellationToken.None); return; }

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var correlation = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlation) || correlation.Length > 128) correlation = Guid.NewGuid().ToString("N");
    context.TraceIdentifier = correlation; context.Response.Headers["X-Correlation-ID"] = correlation;
    using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlation)) await next();
});
app.UseMiddleware<RequestSecurityMiddleware>();
app.UseMiddleware<SessionAuthMiddleware>();
app.UseMiddleware<SessionStateBoundaryMiddleware>();
app.UseMiddleware<F2IdempotencyMiddleware>();
app.UseRateLimiter();
app.MapAuthEndpoints();
app.MapF2Endpoints();
app.MapF3Endpoints();
app.MapF4Endpoints();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.Run();

public partial class Program;
