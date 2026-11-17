using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// VERITABANI-DAGITIM-11: billing.invoices keeps a provider's ETTN once per tenant and an invoice number once per issuer.
// A save refused by either index means the provider answered with an ETTN or a number another invoice already holds.
// That is no race a retry settles: the job stops for the owner's review with a plain message, and the invoice is never
// sent to the provider again because of it.
public static class InvoiceProviderIdentityConflict
{
    public const string Code = "INVOICE_PROVIDER_IDENTITY_TAKEN";

    public const string Message = "E-fatura sağlayıcısı, panelde başka bir faturada kayıtlı olan bir fatura numarası veya ETTN döndürdü. Fatura yeniden gönderilmedi; iki faturayı sağlayıcı portalında karşılaştırıp inceleyin.";

    public const string EttnIndex = "UX_invoices_TenantId_EttnUuid";
    public const string InvoiceNumberIndex = "UX_invoices_TenantId_LegalEntityProfileId_InvoiceNumber";

    public static bool Matches(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
                && postgres.ConstraintName is EttnIndex or InvoiceNumberIndex)
                return true;
        return false;
    }
}
