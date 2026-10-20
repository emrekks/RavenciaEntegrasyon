using System.Text.Json;
using MarketplaceHub.Api.Invoicing;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Invoicing findings of the 20 Oct 2026 deep audit that need no database.
public sealed class InvoicingDeepAuditTests
{
    // G1: a delivery never starts for an invoice that is being cancelled, was cancelled, refused or must be adjusted.
    [Theory]
    [InlineData(InvoiceStatus.Cancelled)]
    [InlineData(InvoiceStatus.CancellationPending)]
    [InlineData(InvoiceStatus.CancellationRejected)]
    [InlineData(InvoiceStatus.AdjustmentRequired)]
    [InlineData(InvoiceStatus.CancelledLocal)]
    [InlineData(InvoiceStatus.Rejected)]
    public void Delivery_is_refused_for_an_invoice_outside_delivery(InvoiceStatus status)
    {
        Assert.True(InvoiceDeliveryGuardPolicy.InvoiceBlocksDelivery(status));
        Assert.Null(InvoiceDeliveryGuardPolicy.OutcomeStatus(status, allTargetsConfirmed: true, issuedAtProvider: true));
    }

    [Theory]
    [InlineData(InvoiceStatus.Accepted)]
    [InlineData(InvoiceStatus.MarketplacePending)]
    [InlineData(InvoiceStatus.MarketplaceFailed)]
    [InlineData(InvoiceStatus.ManualReview)]
    [InlineData(InvoiceStatus.Completed)]
    public void Delivery_runs_for_an_issued_invoice_on_its_way(InvoiceStatus status) =>
        Assert.False(InvoiceDeliveryGuardPolicy.InvoiceBlocksDelivery(status));

    [Theory]
    [InlineData("REVOCATION_REQUIRED", true)]
    [InlineData("REVOCATION_STARTED", true)]
    [InlineData("REVOCATION_UNKNOWN", true)]
    [InlineData("LINK_REMOVED", true)]
    [InlineData("REVOKED", false)]
    [InlineData("UNKNOWN", false)]
    [InlineData(null, false)]
    public void A_link_in_removal_is_never_sent_again(string? deliveryStatus, bool blocked) =>
        Assert.Equal(blocked, InvoiceDeliveryGuardPolicy.StateBlocksDelivery(deliveryStatus));

    [Fact]
    public void Delivery_outcome_follows_the_state_machine()
    {
        Assert.Equal(InvoiceStatus.Completed, InvoiceDeliveryGuardPolicy.OutcomeStatus(InvoiceStatus.MarketplacePending, true, true));
        Assert.Equal(InvoiceStatus.MarketplacePending, InvoiceDeliveryGuardPolicy.OutcomeStatus(InvoiceStatus.Accepted, false, true));
        // A failed delivery that a later read-back confirms goes through MarketplacePending.
        Assert.Equal(InvoiceStatus.Completed, InvoiceDeliveryGuardPolicy.OutcomeStatus(InvoiceStatus.MarketplaceFailed, true, true));
        // A reviewed delivery of an issued invoice may be confirmed; a review before issuing may not.
        Assert.Equal(InvoiceStatus.Completed, InvoiceDeliveryGuardPolicy.OutcomeStatus(InvoiceStatus.ManualReview, true, true));
        Assert.Null(InvoiceDeliveryGuardPolicy.OutcomeStatus(InvoiceStatus.ManualReview, true, false));
        Assert.Null(InvoiceDeliveryGuardPolicy.OutcomeStatus(InvoiceStatus.Draft, true, false));
    }

    // G28: a package whose link or number names another invoice is never cleared.
    [Fact]
    public void Link_removal_requires_both_the_link_and_the_number_to_be_ours_when_present()
    {
        var command = new InvoiceLinkRevocationCommand(new("1"), ["https://panel.example.test/invoice/a"], "GIB2026000000001");
        Assert.True(TrendyolHttpClient.HoldsThisInvoice(new("Invoiced", "GIB2026000000001", "https://panel.example.test/invoice/a/", null), command));
        Assert.True(TrendyolHttpClient.HoldsThisInvoice(new("Invoiced", null, "https://panel.example.test/invoice/a", null), command));
        Assert.True(TrendyolHttpClient.HoldsThisInvoice(new("Invoiced", "GIB2026000000001", null, null), command));
        Assert.False(TrendyolHttpClient.HoldsThisInvoice(new("Invoiced", "GIB2026000000001", "https://other.example.test/live.pdf", null), command));
        Assert.False(TrendyolHttpClient.HoldsThisInvoice(new("Invoiced", "GIB2026000000099", "https://panel.example.test/invoice/a", null), command));
        Assert.False(TrendyolHttpClient.HoldsThisInvoice(new("Invoiced", null, null, null), command));
    }

    // C24: the documented send-invoice-link body.
    [Fact]
    public void Trendyol_invoice_link_body_sends_the_package_as_a_number_and_drops_panel_fields()
    {
        using var payload = JsonDocument.Parse("""{"shipmentPackageId":"3361298374","invoiceLink":"https://panel.example.test/x/content","invoiceDateTime":1760000000000,"invoiceNumber":"GIB2026000000001","micro":false,"orderNumber":"1"}""");

        var body = TrendyolHttpClient.InvoiceLinkBody(payload.RootElement);

        Assert.NotNull(body);
        var json = JsonSerializer.Serialize(body);
        using var sent = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Number, sent.RootElement.GetProperty("shipmentPackageId").ValueKind);
        Assert.Equal(3361298374L, sent.RootElement.GetProperty("shipmentPackageId").GetInt64());
        Assert.Equal("GIB2026000000001", sent.RootElement.GetProperty("invoiceNumber").GetString());
        Assert.False(sent.RootElement.TryGetProperty("micro", out _));
        Assert.False(sent.RootElement.TryGetProperty("orderNumber", out _));
    }

    [Theory]
    [InlineData("""{"shipmentPackageId":"pkg-1","invoiceLink":"https://x.test/a"}""")]
    [InlineData("""{"shipmentPackageId":"1","invoiceLink":"https://x.test/a","invoiceNumber":"INV-1"}""")]
    [InlineData("""{"shipmentPackageId":"1","invoiceLink":"https://x.test/a","invoiceNumber":"GIB20260000000"}""")]
    public void Trendyol_invoice_link_body_refuses_a_package_or_invoice_number_that_does_not_fit(string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        Assert.Null(TrendyolHttpClient.InvoiceLinkBody(payload.RootElement));
    }

    // C22: recipientInfo fits the documented limits and never carries an empty district.
    [Fact]
    public void Recipient_fields_are_normalised_to_the_documented_limits()
    {
        var recipient = new EfaturamRecipient("11111111111", "TR", " İstanbul ", "Şişli", new string('A', 300), "34 380", "+90 (532) 111 22 33", " Ali@Example.TEST ", new string('N', 140), "Veli", null);

        var normalized = EfaturamRecipientNormalizer.Normalize(recipient);

        Assert.Equal("İstanbul", normalized.City);
        Assert.Equal(EfaturamRecipientNormalizer.AddressMaxLength, normalized.Address.Length);
        Assert.Equal(EfaturamRecipientNormalizer.NameMaxLength, normalized.Name!.Length);
        Assert.Equal("34380", normalized.PostalCode);
        Assert.Equal("5321112233", normalized.Phone);
        Assert.Equal("ali@example.test", normalized.Email);
        Assert.Null(EfaturamRecipientNormalizer.PostalCode("3438"));
        Assert.Null(EfaturamRecipientNormalizer.Phone("12345"));
        Assert.Equal("5321112233", EfaturamRecipientNormalizer.Phone("05321112233"));
        Assert.Null(EfaturamRecipientNormalizer.Email("not-an-email"));
    }

    [Theory]
    [InlineData("İstanbul", "", EfaturamRecipientNormalizer.DistrictRequiredCode)]
    [InlineData("İ", "Şişli", EfaturamRecipientNormalizer.CityRequiredCode)]
    public void A_missing_city_or_district_stops_the_request(string city, string district, string code)
    {
        var recipient = new EfaturamRecipient("11111111111", "TR", city, district, "Adres", null, null, null, "Ali", "Veli", null);
        Assert.Equal(code, Assert.Throws<ArgumentException>(() => EfaturamRecipientNormalizer.Normalize(recipient)).Message);
    }

    // C16: the documented outgoing E-Invoice status read is a GET by UUID.
    [Fact]
    public void Status_reads_use_the_documented_uuid_paths()
    {
        Assert.Equal("api/invoice/documents/earchive/status/abc", TrendyolEFaturamEndpoints.Status("EARSIVFATURA", "abc"));
        Assert.Equal("api/invoice/documents/outgoing-einvoice/status/abc", TrendyolEFaturamEndpoints.Status("TEMELFATURA", "abc"));
        // Issuing an E-Invoice stays closed (owner decision).
        Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientCode, InvoiceIssuePolicy.Refusal("TEMELFATURA", "TRY", "{}")?.Code);
    }

    // C5 reverted: Hepsiburada keeps the live-proven invoice body (MIME contentType, no number parts) until SIT says otherwise.
    [Fact]
    public void Hepsiburada_invoice_body_keeps_the_live_proven_mime_content_type()
    {
        Assert.Equal("application/pdf", HepsiburadaInvoiceDeliveryPolicy.DocumentContentType("pdf"));
        Assert.Equal("text/html", HepsiburadaInvoiceDeliveryPolicy.DocumentContentType("TEXT/HTML"));
        Assert.Null(HepsiburadaInvoiceDeliveryPolicy.DocumentContentType("image/png"));

        var body = HepsiburadaInvoiceDeliveryPolicy.Body(new("p", "o", new Uri("https://x.test/a.pdf"), "2026-10-20T09:00:00.0000000+00:00", "application/pdf", "DAP2026000000016"));
        using var document = JsonDocument.Parse(body);
        var invoice = document.RootElement.GetProperty("invoices")[0];
        Assert.Equal("application/pdf", invoice.GetProperty("contentType").GetString());
        Assert.False(invoice.TryGetProperty("serialNumber", out _));
        Assert.False(invoice.TryGetProperty("rowNumber", out _));
    }

    // G36: only codes proven to stop before the provider make a Submitting invoice retryable.
    [Fact]
    public void An_unknown_error_code_keeps_the_submission_in_progress()
    {
        Assert.False(InvoicingBillingService.IsStoppedBeforeProvider(InvoiceStatus.Submitting, "LEASE_EXPIRED", null));
        Assert.Equal("FATURA_ISLENIYOR", InvoicingBillingService.FiscalSubmissionLabel(InvoiceStatus.Submitting, "LEASE_EXPIRED", null));
        Assert.True(InvoicingBillingService.IsStoppedBeforeProvider(InvoiceStatus.Submitting, MarketplaceInvoiceCreationPolicy.DisabledErrorCode, null));
    }

    // G20: the orders page and the invoice workspace label the same fiscal states alike.
    [Theory]
    [InlineData(InvoiceStatus.Submitting, "EFATURAM_OPERATION_FORBIDDEN", "FATURA_REDDEDILDI")]
    [InlineData(InvoiceStatus.Submitting, null, "FATURA_ISLENIYOR")]
    [InlineData(InvoiceStatus.UnknownResult, "INVOICE_SUBMISSION_OUTCOME_UNKNOWN", "FATURA_BELIRSIZ")]
    [InlineData(InvoiceStatus.AdjustmentRequired, "INVOICE_ADJUSTMENT_REQUIRED", "FATURA_DUZELTME_GEREKLI")]
    public void Orders_and_workspace_share_the_fiscal_label(InvoiceStatus status, string? errorCode, string expected)
    {
        var invoice = new Invoice
        {
            Status = status,
            LastErrorCode = errorCode,
            InvoiceNumber = status == InvoiceStatus.AdjustmentRequired ? "GIB2026000000001" : null,
            InvoiceType = "EARSIVFATURA",
            SequencePurpose = "SALE",
            Currency = "TRY",
            Note = string.Empty,
            IdempotencyKey = "label"
        };

        Assert.Equal(expected, InvoicingBillingService.SharedFiscalLabel(status, errorCode, null));
        Assert.Equal(expected, MarketplaceSalesService.InvoiceLabelForPlatform(invoice, MarketplaceInvoiceStatus.Invoiced, "{}", [], "TRENDYOL"));
    }

    // G17: the inline budget starts after the batch is queued, so a long queue does not use it up.
    [Fact]
    public async Task Workspace_confirm_runs_the_first_items_inline_even_after_a_long_queue()
    {
        var time = new ManualTime();
        var lifetime = new Lifetime();
        using var client = new CancellationTokenSource();
        var service = new SlowConfirmService(time, client);
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IHostApplicationLifetime>(lifetime)
                .AddSingleton<TimeProvider>(time)
                .AddSingleton<ITenantContextAccessor>(new TenantAccessor(new(Guid.NewGuid(), Guid.NewGuid(), "OWNER")))
                .BuildServiceProvider(),
            RequestAborted = client.Token
        };
        http.Request.Headers["Idempotency-Key"] = "confirm-1";

        var result = await InvoicingEndpoints.ConfirmWorkspaceAsync(new([], true), http, service, new CompletingRunner());

        var ok = Assert.IsType<Ok<InvoiceWorkspaceConfirmResult>>(result);
        Assert.All(ok.Value!.Items, item => Assert.Equal("COMPLETED", item.Status));
        // The browser left while the batch was queued; the server's token was not cancelled.
        Assert.False(service.TokenSeen.IsCancellationRequested);
    }

    private sealed class SlowConfirmService(ManualTime time, CancellationTokenSource client) : IInvoicingBillingService
    {
        public CancellationToken TokenSeen { get; private set; }

        public Task<ServiceResult<InvoiceWorkspaceConfirmResult>> ConfirmWorkspaceInvoicesAsync(Guid tenantId, InvoiceWorkspacePreviewConfirmRequest request, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
        {
            TokenSeen = cancellationToken;
            client.Cancel();
            time.Now += InlineExecutionBudget.Budget + InlineExecutionBudget.Grace;
            return Task.FromResult(ServiceResult<InvoiceWorkspaceConfirmResult>.Ok(new([
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "QUEUED", "SUBMIT", "Sırada"),
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "QUEUED", "SUBMIT", "Sırada")])));
        }

        public Task<ServiceResult<InvoicePolicyView>> GetPolicyAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<InvoicePolicyView>> UpsertPolicyAsync(Guid tenantId, Guid connectionId, long? expectedVersion, UpsertInvoicePolicyCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PageResult<InvoiceListView>> ListAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<InvoiceWorkspaceItemView>> WorkspaceAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InvoiceWorkspacePageView> WorkspacePageAsync(Guid tenantId, InvoiceWorkspacePageQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InvoiceWorkspaceSummaryView> WorkspaceSummaryAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<bool>> ConfirmMarketplaceDeliveredAsync(Guid tenantId, Guid orderId, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<bool>> ReplaceCancelledInvoiceAsync(Guid tenantId, Guid orderId, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<bool>> ConfirmNoRemoteInvoiceAsync(Guid tenantId, Guid orderId, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<Guid>> EnqueueOrderInvoiceReconciliationAsync(Guid tenantId, Guid orderId, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<IReadOnlyList<InvoiceWorkspacePreviewItem>>> PreviewWorkspaceInvoicesAsync(Guid tenantId, InvoiceWorkspacePreviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<InvoiceDetailView>> CreateDraftAsync(Guid tenantId, CreateInvoiceCommand command, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<InvoiceDetailView>> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<InvoiceDetailView>> ValidateAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<Guid>> EnqueueSubmitAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<Guid>> EnqueueStageCapabilityProbeAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<Guid>> EnqueueReconcileAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<Guid>> EnqueueDeliveryAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<Guid>> EnqueueCancellationAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<MarketplaceLinkRevocationView>> RevokeMarketplaceLinkAsync(Guid tenantId, Guid invoiceId, Guid deliveryStateId, string idempotencyKey, string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<(Stream Content, string MimeType, string FileName)>> OpenDocumentAsync(Guid tenantId, Guid invoiceId, Guid documentId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CompletingRunner : IInvoiceInlineRunner
    {
        public Task<InlineInvoiceOutcome> RunAsync(Guid tenantId, Guid invoiceId, Guid firstJobId, DateTimeOffset deadline, CancellationToken cancellationToken) =>
            Task.FromResult(new InlineInvoiceOutcome("COMPLETED", "Kesildi"));
    }

    private sealed class TenantAccessor(TenantContext context) : ITenantContextAccessor
    {
        public TenantContext? Current => context;
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
    }
}
