using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Bootstrap;

public sealed class BootstrapService(AppDbContext db, UserManager<ApplicationUser> users, IOptions<BootstrapOptions> options, TimeProvider timeProvider)
{
    private const long AdvisoryLockKey = 6_527_314_102;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        if (!value.Enabled) throw new InvalidOperationException("Bootstrap is disabled.");
        if (string.IsNullOrWhiteSpace(value.OwnerPassword)) throw new InvalidOperationException("Bootstrap owner password secret is missing.");
        if (value.OwnerPassword.Length is < 15 or > 64) throw new InvalidOperationException("Bootstrap owner password must contain 15-64 characters.");

        await db.Database.OpenConnectionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({AdvisoryLockKey})", cancellationToken);
        try
        {
            var fingerprint = Fingerprint(value);
            var marker = await db.BootstrapStates.SingleOrDefaultAsync(x => x.Key == "initial-owner", cancellationToken);
            if (marker is not null)
            {
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(marker.ConfigurationFingerprint), Convert.FromHexString(fingerprint)))
                    throw new InvalidOperationException("Bootstrap configuration differs from the persistent marker.");
                return;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            var tenant = new Tenant { Id = Guid.NewGuid(), Code = value.TenantCode, DisplayName = value.TenantDisplayName, CreatedAt = now, UpdatedAt = now };
            db.Tenants.Add(tenant);
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = value.OwnerEmail,
                Email = value.OwnerEmail,
                EmailConfirmed = true,
                DisplayName = value.OwnerDisplayName,
                ForcePasswordChange = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            var result = await users.CreateAsync(user, value.OwnerPassword);
            if (!result.Succeeded) throw new InvalidOperationException("Owner creation failed: " + string.Join(", ", result.Errors.Select(x => x.Code)));
            db.TenantMemberships.Add(new TenantMembership { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = user.Id, Role = MembershipRole.Owner, CreatedAt = now, UpdatedAt = now });
            db.UserSecurities.Add(new UserSecurity { UserId = user.Id });
            db.FeatureFlags.Add(new FeatureFlag { Key = "external-writes", Enabled = false, UpdatedAt = now });
            db.BootstrapStates.Add(new BootstrapState { Key = "initial-owner", CompletedAt = now, TenantId = tenant.Id, OwnerUserId = user.Id, ConfigurationFingerprint = fingerprint });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({AdvisoryLockKey})", cancellationToken);
            await db.Database.CloseConnectionAsync();
        }
    }

    private static string Fingerprint(BootstrapOptions value)
    {
        var normalized = string.Join('|', value.TenantCode.Trim().ToLowerInvariant(), value.TenantDisplayName.Trim(), value.OwnerEmail.Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
