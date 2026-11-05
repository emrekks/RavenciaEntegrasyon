using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Security;

// A public invoice link (ours, /api/v1/public/...-invoice-documents/{token}, or the marketplace's own) is a bearer
// secret: whoever holds it opens the invoice without a session. The order and invoice lists are readable by every
// role, so a role that may not open invoice documents in the panel (READONLY) gets them without the links (YENİ-02).
public static class InvoiceLinkVisibility
{
    public static PageResult<OrderListView> ForRole(PageResult<OrderListView> page, string? role) =>
        RoleAuthorizationMiddleware.CanOpenInvoiceDocuments(role)
            ? page
            : page with { Items = page.Items.Select(item => item with { InvoiceDocumentUrl = null }).ToList() };

    public static ServiceResult<OrderDetailView> ForRole(ServiceResult<OrderDetailView> result, string? role) =>
        RoleAuthorizationMiddleware.CanOpenInvoiceDocuments(role) || result.Value is null
            ? result
            : result with { Value = result.Value with { InvoiceDocumentUrl = null } };

    public static InvoiceWorkspacePageView ForRole(InvoiceWorkspacePageView page, string? role) =>
        RoleAuthorizationMiddleware.CanOpenInvoiceDocuments(role)
            ? page
            : page with
            {
                Items = page.Items.Select(item => item with
                {
                    MarketplaceInvoiceUrl = null,
                    ManualInvoiceDocumentUrl = null,
                    CurrentPlatformInvoiceUrl = null,
                    PreviousPlatformInvoiceReferences = item.PreviousPlatformInvoiceReferences?.Select(reference => reference with { InvoiceUrl = null }).ToList()
                }).ToList()
            };
}
