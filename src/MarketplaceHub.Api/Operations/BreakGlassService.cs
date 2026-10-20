using System.Security.Cryptography;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Operations;

// Host-side recovery for a single owner who cannot sign in: run inside the API container with
// MARKETPLACEHUB_BREAK_GLASS_AUTHORIZED=true and a reason, e.g.
//   identity reset-mfa <email> "<reason>"
//   identity reset-password <email> "<reason>"
public sealed class BreakGlassService(AppDbContext db, TimeProvider timeProvider, UserManager<ApplicationUser> users)
{
    public async Task ResetMfaAsync(string email, string reason, CancellationToken cancellationToken)
    {
        EnsureAuthorized(reason);
        var user = await FindAsync(email, cancellationToken);
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == user.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        security.TotpState = TotpState.Disabled; security.ProtectedTotpSecret = null; security.EnrollmentExpiresAt = null; security.PendingProtectedTotpSecret = null; security.PendingEnrollmentExpiresAt = null; security.LastAcceptedTimeStep = null; security.RecoveryBatchId = null; security.Version++;
        await db.RecoveryCodes.Where(x => x.UserId == user.Id && x.InvalidatedAt == null).ExecuteUpdateAsync(x => x.SetProperty(c => c.InvalidatedAt, now), cancellationToken);
        user.SessionVersion++;
        await db.UserSessions.Where(x => x.UserId == user.Id && x.State != SessionState.Revoked).ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, now), cancellationToken);
        db.AuditLogs.Add(new AuditLog { ActorUserId = null, Action = "IDENTITY_MFA_BREAK_GLASS_RESET", TargetType = "User", TargetId = user.Id.ToString(), Reason = reason, CorrelationId = Guid.NewGuid().ToString("N"), CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    // Sets a one-time password (returned once, for the operator to hand over), which must be changed at the
    // next sign-in; ends every session and lifts the account lockout. The authenticator stays as it is.
    public async Task<string> ResetPasswordAsync(string email, string reason, CancellationToken cancellationToken)
    {
        EnsureAuthorized(reason);
        var user = await FindAsync(email, cancellationToken);
        var temporary = TemporaryPassword();
        var now = timeProvider.GetUtcNow();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, token, temporary);
        if (!reset.Succeeded) throw new InvalidOperationException("Password reset failed: " + string.Join(", ", reset.Errors.Select(error => error.Code)));
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        // ResetPasswordAsync saved the user through the user store; read the row again before changing it.
        await db.Entry(user).ReloadAsync(cancellationToken);
        user.ForcePasswordChange = true;
        user.SessionVersion++;
        user.UpdatedAt = now;
        await db.UserSessions.Where(x => x.UserId == user.Id && x.State != SessionState.Revoked).ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, now), cancellationToken);
        db.AuditLogs.Add(new AuditLog { ActorUserId = null, Action = "IDENTITY_PASSWORD_BREAK_GLASS_RESET", TargetType = "User", TargetId = user.Id.ToString(), Reason = reason, CorrelationId = Guid.NewGuid().ToString("N"), CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return temporary;
    }

    // 24 characters with every class Identity asks for; the rest from an alphabet without look-alikes.
    internal static string TemporaryPassword()
    {
        const string upper = "ABCDEFGHJKMNPQRSTUVWXYZ", lower = "abcdefghjkmnpqrstuvwxyz", digits = "23456789", symbols = "#%+=?@";
        const string all = upper + lower + digits + symbols;
        var characters = new List<char> { Pick(upper), Pick(lower), Pick(digits), Pick(symbols) };
        while (characters.Count < 24) characters.Add(Pick(all));
        return new string(characters.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray());
        static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
    }

    private static void EnsureAuthorized(string reason)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MARKETPLACEHUB_BREAK_GLASS_AUTHORIZED"), "true", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Break-glass OS authorization is absent.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512) throw new ArgumentException("A bounded audit reason is required.", nameof(reason));
    }

    private async Task<ApplicationUser> FindAsync(string email, CancellationToken cancellationToken) =>
        await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == email.Trim().ToUpperInvariant(), cancellationToken) ?? throw new InvalidOperationException("User not found.");
}
