using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Bootstrap;

public sealed class LocalDevelopmentSeedService(
    AppDbContext db,
    IOptions<LocalDevelopmentSeedOptions> options,
    IPasswordHasher<ApplicationUser> passwordHasher,
    TimeProvider timeProvider)
{
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        if (!value.Enabled) return;
        if (string.IsNullOrWhiteSpace(value.Email) || string.IsNullOrWhiteSpace(value.Password))
            throw new InvalidOperationException("Local development seed credentials are missing.");

        var tenant = await db.Tenants.SingleOrDefaultAsync(x => x.Code == value.TenantCode, cancellationToken);
        if (tenant is null)
        {
            var now = timeProvider.GetUtcNow();
            tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Code = value.TenantCode,
                DisplayName = "Ravencia",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Tenants.Add(tenant);
        }

        var normalizedEmail = value.Email.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        var current = timeProvider.GetUtcNow();
        if (user is null)
        {
            var seededUser = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = value.Email.Trim(),
                NormalizedUserName = normalizedEmail,
                Email = value.Email.Trim(),
                NormalizedEmail = normalizedEmail,
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                DisplayName = value.DisplayName,
                Status = "ACTIVE",
                ForcePasswordChange = false,
                CreatedAt = current,
                UpdatedAt = current
            };
            seededUser.PasswordHash = passwordHasher.HashPassword(seededUser, value.Password);
            user = seededUser;
            db.Users.Add(user);
            db.UserSecurities.Add(new UserSecurity { UserId = user.Id });
        }
        else
        {
            user.Status = "ACTIVE";
            user.UpdatedAt = current;
        }

        var membership = await db.TenantMemberships.SingleOrDefaultAsync(x => x.TenantId == tenant.Id && x.UserId == user.Id, cancellationToken);
        if (membership is null)
        {
            db.TenantMemberships.Add(new TenantMembership
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UserId = user.Id,
                Role = MembershipRole.Owner,
                CreatedAt = current,
                UpdatedAt = current
            });
        }
        else
        {
            membership.Status = RecordStatus.Active;
            membership.Role = MembershipRole.Owner;
            membership.UpdatedAt = current;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
