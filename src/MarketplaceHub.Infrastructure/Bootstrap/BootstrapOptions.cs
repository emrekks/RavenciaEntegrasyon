namespace MarketplaceHub.Infrastructure.Bootstrap;

public sealed class BootstrapOptions
{
    public bool Enabled { get; set; }
    public string TenantCode { get; set; } = "ravencia";
    public string TenantDisplayName { get; set; } = "Ravencia";
    public string OwnerEmail { get; set; } = string.Empty;
    public string OwnerDisplayName { get; set; } = "Ravencia Admin";
    public string? OwnerPassword { get; set; }
}
