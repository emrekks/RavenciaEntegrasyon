namespace MarketplaceHub.Infrastructure.Bootstrap;

public sealed class LocalDevelopmentSeedOptions
{
    public bool Enabled { get; set; }
    public string Email { get; set; } = "admin@ravencia.local";
    public string DisplayName { get; set; } = "Ravencia Local Admin";
    public string Password { get; set; } = "test123";
    public string TenantCode { get; set; } = "ravencia";
}
