using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Operations;

public sealed class BreakGlassService(AppDbContext db, TimeProvider timeProvider)
{
    public async Task ResetMfaAsync(string email, string reason, CancellationToken cancellationToken)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MARKETPLACEHUB_BREAK_GLASS_AUTHORIZED"), "true", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Break-glass OS authorization is absent.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512) throw new ArgumentException("A bounded audit reason is required.", nameof(reason));
        var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == email.Trim().ToUpper(), cancellationToken) ?? throw new InvalidOperationException("User not found.");
        var security = await db.UserSecurities.SingleAsync(x => x.UserId == user.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        security.TotpState = TotpState.Disabled; security.ProtectedTotpSecret = null; security.EnrollmentExpiresAt = null; security.LastAcceptedTimeStep = null; security.RecoveryBatchId = null;
        await db.RecoveryCodes.Where(x => x.UserId == user.Id && x.InvalidatedAt == null).ExecuteUpdateAsync(x => x.SetProperty(c => c.InvalidatedAt, now), cancellationToken);
        user.SessionVersion++;
        await db.UserSessions.Where(x => x.UserId == user.Id && x.State != SessionState.Revoked).ExecuteUpdateAsync(x => x.SetProperty(s => s.State, SessionState.Revoked).SetProperty(s => s.RevokedAt, now), cancellationToken);
        db.AuditLogs.Add(new AuditLog { ActorUserId = null, Action = "IDENTITY_MFA_BREAK_GLASS_RESET", TargetType = "User", TargetId = user.Id.ToString(), Reason = reason, CorrelationId = Guid.NewGuid().ToString("N"), CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }
}
