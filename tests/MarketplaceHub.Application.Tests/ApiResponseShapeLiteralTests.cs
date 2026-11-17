using System.Text.Json;
using MarketplaceHub.Api.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-8, round 5: the named records of answers a handler writes as a fixed literal the test host cannot reach
// (the confirmation steps need an invoice in a given state, a webhook a signed delivery, a backup run a Google
// connection). Each literal here is the handler's own expression; the record written with the API's JSON options must
// be the same bytes. ApiResponseSchemaTests checks the reachable ones against real answers.
public sealed class ApiResponseShapeLiteralTests(ApiPipelineHost fixture) : IClassFixture<ApiPipelineHost>
{
    // The API's own JSON options, as its answers are written.
    private JsonSerializerOptions Api => fixture.Host.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

    private void Same<T>(object literal, T record) => Assert.Equal(JsonSerializer.Serialize(literal, Api), JsonSerializer.Serialize(record, Api));

    [Fact]
    public void FixedLiteralAnswersAreWrittenAsTheirRecords()
    {
        // InvoicingEndpoints: confirm-no-remote-invoice, confirm-marketplace-delivered, replace-cancelled-invoice.
        Same(new { confirmed = true }, new InvoiceConfirmedView(true));
        Same(new { replaced = true }, new InvoiceReplacedView(true));
        // OffsiteBackupEndpoints.RunAsync.
        Same(new { requested = true }, new OffsiteBackupRunRequestedView(true));
        // MarketplaceEndpoints.ReceiveWebhookCore at the base address.
        Same(new { accepted = true }, new WebhookAcceptedView(true));
        // InvoicingEndpoints.QueueManualOrderInvoiceDeliveryAsync when every package already confirmed the document.
        var documentId = Guid.CreateVersion7();
        Same(new { documentId, jobIds = Array.Empty<Guid>(), message = "Belge seçili paketlerin tamamında zaten doğrulanmış." },
            new ManualInvoiceDeliveryConfirmedView(documentId, [], "Belge seçili paketlerin tamamında zaten doğrulanmış."));
    }

    // Round 6: the answers that need a live marketplace, a queued job, Claude, Gmail or Google Drive; each literal is
    // the handler's own expression with representative values (nulls included where the member may be null).
    [Fact]
    public void RoundSixAnswersAreWrittenAsTheirRecords()
    {
        // MarketplaceEndpoints GET /orders/{id}/finance.
        var now = new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);
        IReadOnlyList<MarketplaceHub.Application.RemoteFinanceTransaction> transactions =
            [new("T1", "PAID", 120.5m, null, 0m, 1.25m, 0m, 0m, 0m, 1, now, null), new("T2", "WAITING", 10m, 1.5m, 0m, 0m, 0m, 2m, 0m, 3, now, now)];
        IReadOnlyList<MarketplaceHub.Application.RemoteCommissionInvoice> invoices = [new("O1", null, null, 3.6m, null), new("O1", "KF1", now, 3.6m, "OK")];
        Same(new { readAt = now, transactions, commissionInvoices = invoices }, new OrderFinanceView(now, transactions, invoices));
        // CatalogEndpoints POST /products/{id}/publication-jobs and /update-jobs.
        var jobId = Guid.CreateVersion7();
        IReadOnlyList<MarketplaceHub.Application.PublicationSkippedVariant> skipped = [new("SKU-1", "VARIANT_ON_MARKETPLACE", "Zaten yayında.")];
        Same(new { jobId, skippedVariants = skipped }, new ProductPublicationQueuedView(jobId, skipped));
        IReadOnlyList<string> notices = ["Açıklama karttan gönderildi."];
        Same(new { jobId, notices }, new ProductUpdateQueuedView(jobId, notices));
        // AiEndpoints.TestAsync.
        const string model = "claude-model";
        Same(new { ok = true, model, message = $"Bağlantı çalışıyor: {model} yanıt verdi." }, new AiConnectionTestView(true, model, $"Bağlantı çalışıyor: {model} yanıt verdi."));
        // AlertEmailEndpoints.TestAsync.
        IReadOnlyList<string> recipients = ["a@example.com", "b@example.com"];
        Same(new { sent = true, recipients }, new AlertEmailTestView(true, recipients));
        // OffsiteBackupEndpoints.TestAsync (the folder id may be null).
        string? folderId = "folder-1";
        Same(new { ok = true, folderId }, new OffsiteBackupTestView(true, folderId));
        folderId = null;
        Same(new { ok = true, folderId }, new OffsiteBackupTestView(true, folderId));
    }

    // CommissionTariffCompactRows.Of wrote an anonymous { strings, rows, totalCount, page, pageSize }; the record it
    // returns now writes exactly that, followed by the cursor members of unified paging.
    [Fact]
    public void TheCompactTariffPageIsTheFormerShapeFollowedByTheCursorMembers()
    {
        var compact = CommissionTariffCompactRows.Of(new CommissionTariffRowPage([], 7, 1, 2) { NextCursor = "c", HasMore = true });
        var former = JsonSerializer.Serialize(new { strings = compact.Strings, rows = compact.Rows, totalCount = 7, page = 1, pageSize = 2 }, Api);
        Assert.Equal(former[..^1] + ",\"nextCursor\":\"c\",\"hasMore\":true}", JsonSerializer.Serialize(compact, Api));
    }
}
