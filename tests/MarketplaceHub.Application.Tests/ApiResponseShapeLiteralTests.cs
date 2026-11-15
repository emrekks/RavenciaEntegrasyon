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

    // CommissionTariffCompactRows.Of wrote an anonymous { strings, rows, totalCount, page, pageSize }; the record it
    // returns now writes exactly that.
    [Fact]
    public void TheCompactTariffPageIsTheFormerShape()
    {
        var compact = CommissionTariffCompactRows.Of(new CommissionTariffRowPage([], 7, 1, 2));
        Assert.Equal(JsonSerializer.Serialize(new { strings = compact.Strings, rows = compact.Rows, totalCount = 7, page = 1, pageSize = 2 }, Api), JsonSerializer.Serialize(compact, Api));
    }
}
