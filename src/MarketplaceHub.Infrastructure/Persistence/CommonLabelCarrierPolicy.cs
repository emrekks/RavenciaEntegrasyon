namespace MarketplaceHub.Infrastructure.Persistence;

public static class CommonLabelCarrierPolicy
{
    public static bool Supports(string? cargoProviderExternalId) =>
        !string.IsNullOrWhiteSpace(cargoProviderExternalId) &&
        (cargoProviderExternalId.Contains("ARAS", StringComparison.OrdinalIgnoreCase) ||
         cargoProviderExternalId.Contains("TEX", StringComparison.OrdinalIgnoreCase));

    public static bool SupportsHepsiburada(string? cargoProviderExternalId) =>
        !string.IsNullOrWhiteSpace(cargoProviderExternalId) &&
        (cargoProviderExternalId.Contains("HEPSIJET", StringComparison.OrdinalIgnoreCase) ||
         cargoProviderExternalId.Contains("ARAS", StringComparison.OrdinalIgnoreCase));
}
