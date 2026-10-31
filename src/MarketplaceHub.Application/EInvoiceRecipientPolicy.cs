namespace MarketplaceHub.Application;

/// <summary>What the GİB registered-user list, read through the invoice provider, says about a VKN/TCKN.</summary>
public enum EInvoiceTaxpayerState
{
    Registered,
    NotRegistered,
    Unknown
}

/// <summary>An undeleted INVOICE mailbox (posta kutusu) alias of an e-Invoice taxpayer.</summary>
public sealed record EInvoiceTaxpayerAlias(string Alias, string? PostBoxType);

/// <summary>
/// The provider's answer to a taxpayer lookup. NotRegistered comes only from the provider's documented
/// "tax payer not found" answer; every other failure is Unknown, never a guess.
/// </summary>
public sealed record EInvoiceTaxpayerLookup(EInvoiceTaxpayerState State, string? Title, IReadOnlyList<EInvoiceTaxpayerAlias> InvoiceAliases, string? ErrorCode = null)
{
    public static EInvoiceTaxpayerLookup Unknown(string errorCode) => new(EInvoiceTaxpayerState.Unknown, null, [], errorCode);
    public static EInvoiceTaxpayerLookup NotRegistered() => new(EInvoiceTaxpayerState.NotRegistered, null, []);
}

/// <summary>
/// The document type a sale invoice must have, decided from the taxpayer lookup. TargetAlias is the recipient
/// mailbox an e-Invoice is sent to. A refusal stops the invoice before anything reaches the provider.
/// </summary>
public sealed record EInvoiceRecipientDecision(string InvoiceType, EInvoiceTaxpayerState? TaxpayerState, string? TaxpayerTitle, string? TargetAlias, InvoiceIssueRefusal? Refusal)
{
    /// <summary>An individual buyer without a lookup: the consumer e-Archive, as before.</summary>
    public static EInvoiceRecipientDecision WithoutLookup(string invoiceType) => new(invoiceType, null, null, null, null);
}

public static class EInvoiceRecipientPolicy
{
    public const string EInvoiceType = "TEMELFATURA";
    public const string EArchiveType = "EARSIVFATURA";
    public const string DefaultAliasPrefix = "urn:mail:defaultpk";

    /// <summary>
    /// A corporate buyer, or one the marketplace flags as an e-Invoice taxpayer, is looked up. An individual buyer
    /// gets the consumer e-Archive (its identity is masked as 11111111111), so there is nothing to look up.
    /// </summary>
    public static bool RequiresLookup(string marketplaceInvoiceType, bool corporateRecipient) =>
        corporateRecipient || string.Equals(marketplaceInvoiceType, EInvoiceType, StringComparison.Ordinal);

    /// <summary>
    /// The single alias, else the single "urn:mail:defaultpk" alias; null when the choice is not certain.
    /// E-Faturam refuses an e-Invoice to a buyer with several aliases unless targetAlias names one.
    /// </summary>
    public static string? SelectTargetAlias(IReadOnlyList<EInvoiceTaxpayerAlias> aliases)
    {
        var distinct = aliases.Select(item => item.Alias.Trim()).Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length == 1) return distinct[0];
        var defaults = distinct.Where(alias => alias.StartsWith(DefaultAliasPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        return defaults.Length == 1 ? defaults[0] : null;
    }

    /// <summary>
    /// Registered: e-Invoice to the selected alias. NotRegistered: e-Archive, unless the marketplace flagged the buyer
    /// as an e-Invoice taxpayer (the two sources disagree, so a person decides). Unknown: no invoice.
    /// </summary>
    public static EInvoiceRecipientDecision Decide(string marketplaceInvoiceType, bool corporateRecipient, EInvoiceTaxpayerLookup? lookup)
    {
        if (!RequiresLookup(marketplaceInvoiceType, corporateRecipient)) return EInvoiceRecipientDecision.WithoutLookup(marketplaceInvoiceType);
        switch (lookup?.State)
        {
            case EInvoiceTaxpayerState.Registered:
                var alias = SelectTargetAlias(lookup.InvoiceAliases);
                return new(EInvoiceType, EInvoiceTaxpayerState.Registered, lookup.Title, alias, alias is null ? InvoiceIssuePolicy.EInvoiceTargetAliasUnresolved : null);
            case EInvoiceTaxpayerState.NotRegistered:
                return string.Equals(marketplaceInvoiceType, EInvoiceType, StringComparison.Ordinal)
                    ? new(EInvoiceType, EInvoiceTaxpayerState.NotRegistered, null, null, InvoiceIssuePolicy.EInvoiceTaxpayerConflict)
                    : new(EArchiveType, EInvoiceTaxpayerState.NotRegistered, null, null, null);
            default:
                return new(marketplaceInvoiceType, EInvoiceTaxpayerState.Unknown, null, null, InvoiceIssuePolicy.EInvoiceRecipientReview);
        }
    }
}
