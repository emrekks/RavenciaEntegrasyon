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
        // A Hepsiburada buyer whose kind is not clear waits for the owner (16 Nov 2026): no lookup, no invoice.
        if (TrendyolEFaturamCanonicalPayload.IsRecipientKindAmbiguous(customerSnapshotJson, invoiceAddressSnapshotJson))
            return new(marketplaceType, null, null, null, InvoiceIssuePolicy.RecipientKindUnknown);
        // The identity the e-Invoice would be sent to (never the consumer placeholder) and the buyer kind. The kind the
        // marketplace states decides (report item 12); without it the wider reading (a VKN, a company name next to a tax
        // or identity number) still asks the taxpayer lookup.
        var (taxId, corporateRecipient) = TrendyolEFaturamCanonicalPayload.ResolveRecipientTaxIdentity(customerSnapshotJson, invoiceAddressSnapshotJson, EInvoiceRecipientPolicy.EInvoiceType);
        var corporate = TrendyolEFaturamCanonicalPayload.ExplicitBuyerKind(customerSnapshotJson, invoiceAddressSnapshotJson)
            ?? corporateRecipient || InvoiceAmounts.IsCorporateBuyer(customerSnapshotJson, invoiceAddressSnapshotJson);
        if (!EInvoiceRecipientPolicy.RequiresLookup(marketplaceType, corporate))
            return WithPlaceholderGuard(EInvoiceRecipientDecision.WithoutLookup(marketplaceType), customerSnapshotJson, invoiceAddressSnapshotJson);
        var lookup = provider is null
            ? EInvoiceTaxpayerLookup.Unknown("EINVOICE_TAXPAYER_LOOKUP_UNAVAILABLE")
            : await provider.LookupTaxpayerAsync(providerContext, taxId, fresh, cancellationToken);
        return WithPlaceholderGuard(EInvoiceRecipientPolicy.Decide(marketplaceType, corporate, lookup), customerSnapshotJson, invoiceAddressSnapshotJson);
    }

    // The guard of 16 Nov 2026 for every marketplace, at preview and at issue alike: an invoice whose recipient would be
    // the consumer placeholder 11111111111 while the order holds a company's valid VKN is refused.
    private static EInvoiceRecipientDecision WithPlaceholderGuard(EInvoiceRecipientDecision decision, string customerSnapshotJson, string invoiceAddressSnapshotJson) =>
        decision.Refusal is null
        && TrendyolEFaturamCanonicalPayload.SendsPlaceholderToCorporateTaxNumber(customerSnapshotJson, invoiceAddressSnapshotJson, decision.InvoiceType)
            ? decision with { Refusal = InvoiceIssuePolicy.RecipientPlaceholderForCorporate }
            : decision;
}
