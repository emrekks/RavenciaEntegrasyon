using Microsoft.AspNetCore.Identity;

namespace MarketplaceHub.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public required string DisplayName { get; set; }
    public string Status { get; set; } = "ACTIVE";
    public bool ForcePasswordChange { get; set; } = true;
    public long SessionVersion { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
