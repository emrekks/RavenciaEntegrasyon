using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Api.Common.EndpointHelpers;

namespace MarketplaceHub.Api.Marketplace;

public static partial class MarketplaceEndpoints
{
    // If-Match carries the order version the operator saw (audit 2026-10-20 S20).
    private static async Task<IResult> UpdateShopifyOrderStatusAsync(Guid id, ShopifyOrderStatusCommand command, HttpContext http, IMarketplaceSalesService service)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;
        if (!TryIfMatch(http, out var version, out var failure)) return failure!;
        return WithEtag(http, await service.SetShopifyOrderStatusAsync(tenant.TenantId, tenant.UserId, id, version, command.Status, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), x => x.Version);
    }

    private sealed record ShopifyOrderStatusCommand(string Status);

    private static async Task<IResult> ImportShopifyOrdersAsync(HttpContext http, IShopifyOrderCsvImportService service)
    {
        const long maximumFileBytes = 10 * 1024 * 1024;
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;
        var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = maximumFileBytes + 128 * 1024;
        if (http.Request.ContentLength is > maximumFileBytes + 128 * 1024)
            return Problem(http, new("SHOPIFY_CSV_TOO_LARGE", "Shopify CSV dosyası en fazla 10 MiB olabilir.", 413));
        if (!http.Request.HasFormContentType)
            return Problem(http, new("SHOPIFY_CSV_FORM_REQUIRED", "multipart/form-data biçiminde bir CSV dosyası gönderilmelidir.", 400));

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0)
            return Problem(http, new("SHOPIFY_CSV_FILE_REQUIRED", "Shopify sipariş dışa aktarım CSV dosyasını seçin.", 400));
        if (file.Length > maximumFileBytes)
            return Problem(http, new("SHOPIFY_CSV_TOO_LARGE", "Shopify CSV dosyası en fazla 10 MiB olabilir.", 413));
        if (!string.Equals(Path.GetExtension(Path.GetFileName(file.FileName)), ".csv", StringComparison.OrdinalIgnoreCase))
            return Problem(http, new("SHOPIFY_CSV_EXTENSION_INVALID", "Yalnız .csv uzantılı Shopify dışa aktarım dosyaları kabul edilir.", 415));
        Guid? connectionId = null;
        var connectionValue = form["connectionId"].ToString();
        if (!string.IsNullOrWhiteSpace(connectionValue))
        {
            if (!Guid.TryParse(connectionValue, out var parsedConnectionId))
                return Problem(http, new("SHOPIFY_CONNECTION_ID_INVALID", "Shopify bağlantı seçimi geçersiz.", 400));
            connectionId = parsedConnectionId;
        }

        await using var input = file.OpenReadStream();
        var result = await service.ImportAsync(tenant.TenantId, tenant.UserId, connectionId, input, OperationCorrelation(http), http.RequestAborted);
        return Result(result, Results.Ok);
    }
}
