using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Marketplace;

public static partial class MarketplaceEndpoints
{
    // The connection's data goes in the delete; its settings, keys and the row itself right after (ConnectionLeftovers),
    // and the hourly sweep finishes what a failure here leaves.
    // Resets that empty data take the caller's password again (and the authenticator code when it is on); one passed check
    // covers the session's resets for a few minutes (AuthEndpoints.ReauthenticateForResetAsync). The secrets never reach
    // the services.
    private static async Task<IResult> FullResetConnectionAsync(Guid id, FullResetConnectionCommand command, HttpContext http, IOperationalDataMaintenanceService service,
        UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
        if (!TryIfMatch(http, out var version, out var failure)) return failure!;
        if (await AuthEndpoints.ReauthenticateForResetAsync(command.Password, command.Code, command.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;
        return Result(await service.RequestFullResetAsync(tenant.TenantId, tenant.UserId, id, version, command with { Password = null, Code = null, RecoveryCode = null }, http.TraceIdentifier, http.RequestAborted), Results.Ok);
    }

    private static async Task<IResult> FactoryResetAsync(PanelFactoryResetCommand command, HttpContext http, IPanelFactoryResetService service,
        UserManager<ApplicationUser> users, AppDbContext db, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher, TimeProvider time)
    {
        if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
        if (await AuthEndpoints.ReauthenticateForResetAsync(command.Password, command.Code, command.RecoveryCode, http, users, db, protection, totp, hasher, time) is { } refusal) return refusal;
        return Result(await service.RequestAsync(tenant.TenantId, tenant.UserId, (http.Items["UserSession"] as UserSession)?.Id, command with { Password = null, Code = null, RecoveryCode = null }, http.TraceIdentifier, http.RequestAborted), Results.Ok);
    }

    private static async Task<IResult> DeepDeleteConnectionAsync(Guid id, DeleteConnectionCommand command, HttpContext http, IOperationalDataMaintenanceService service, AppDbContext db, TimeProvider timeProvider, ILoggerFactory loggers,
        UserManager<ApplicationUser> users, IDataProtectionProvider protection, TotpService totp, TokenHasher hasher)
    {
        if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
        if (!TryIfMatch(http, out var version, out var failure)) return failure!;
        if (await AuthEndpoints.ReauthenticateForResetAsync(command.Password, command.Code, command.RecoveryCode, http, users, db, protection, totp, hasher, timeProvider) is { } refusal) return refusal;
        command = command with { Password = null, Code = null, RecoveryCode = null };
        var result = await service.DeleteConnectionAsync(tenant.TenantId, tenant.UserId, id, version, command, http.TraceIdentifier, http.RequestAborted);
        if (result.Succeeded)
        {
            try
            {
                db.ChangeTracker.Clear();
                await ConnectionLeftovers.PurgeAsync(db, tenant.TenantId, id, timeProvider.GetUtcNow(), http.RequestAborted);
            }
            catch (Exception exception) when (exception is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
            {
                loggers.CreateLogger("MarketplaceHub.Api.ConnectionDelete").LogWarning(exception, "Silinen bağlantının ayarları hemen kaldırılamadı; saatlik temizlik kaldıracak. ConnectionId: {ConnectionId}", id);
            }
        }
        return Result(result, Results.Ok);
    }
}
