using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// The sale invoice's document type for an order: the marketplace's own word, then the E-Faturam taxpayer lookup for a
/// corporate buyer (<see cref="EInvoiceRecipientPolicy"/>). The preview may use a cached answer; the request that issues
/// the invoice asks afresh.
/// </summary>
internal static class EInvoiceRecipientResolver
{
    public static async Task<EInvoiceRecipientDecision> ResolveAsync(
        IInvoiceProviderPort? provider,
        AdapterContext providerContext,
        string? platformCode,
        string customerSnapshotJson,
        string invoiceAddressSnapshotJson,
        bool fresh,
        CancellationToken cancellationToken)
    {
        var marketplaceType = InvoiceAmounts.InvoiceDocumentType(platformCode, customerSnapshotJson, invoiceAddressSnapshotJson);
        // The identity the e-Invoice would be sent to (never the consumer placeholder) and both corporate readings.
        var (taxId, corporateRecipient) = TrendyolEFaturamCanonicalPayload.ResolveRecipientTaxIdentity(customerSnapshotJson, invoiceAddressSnapshotJson, EInvoiceRecipientPolicy.EInvoiceType);
        var corporate = corporateRecipient || InvoiceAmounts.IsCorporateBuyer(customerSnapshotJson, invoiceAddressSnapshotJson);
        if (!EInvoiceRecipientPolicy.RequiresLookup(marketplaceType, corporate)) return EInvoiceRecipientDecision.WithoutLookup(marketplaceType);
        var lookup = provider is null
            ? EInvoiceTaxpayerLookup.Unknown("EINVOICE_TAXPAYER_LOOKUP_UNAVAILABLE")
            : await provider.LookupTaxpayerAsync(providerContext, taxId, fresh, cancellationToken);
        return EInvoiceRecipientPolicy.Decide(marketplaceType, corporate, lookup);
    }
}
