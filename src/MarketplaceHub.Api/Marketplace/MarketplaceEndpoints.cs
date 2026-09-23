using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Marketplace;

public static class MarketplaceEndpoints
{
    public static IEndpointRouteBuilder MapMarketplaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter(async (context, next) => { try { return await next(context); } catch (ArgumentException exception) { return Problem(context.HttpContext, new("INVALID_CURSOR", exception.Message, 400)); } });
        api.MapGet("/connections", async (HttpContext http, IMarketplaceConnectionService service, int? limit, string? after) => Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http));
        api.MapPost("/connections", async (CreateConnectionCommand command, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateAsync(tenant.TenantId, command, http.RequestAborted), "/api/v1/connections") : MissingContext(http));
        api.MapGet("/connections/{id:guid}", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPatch("/connections/{id:guid}", async (Guid id, UpdateConnectionCommand command, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.UpdateAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure! : Unauthorized(http));
        api.MapPut("/connections/{id:guid}/credential", async (Guid id, CredentialCommand command, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.RotateCredentialAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure! : MissingContext(http));
        api.MapPost("/connections/{id:guid}/test-jobs", async (Guid id, HttpContext http, AppDbContext db, IMarketplaceJobProcessor marketplaceProcessor, IInvoicingJobProcessor invoicingProcessor) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            var platform = await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId && x.Id == id)
                .Select(x => x.PlatformCode)
                .SingleOrDefaultAsync(http.RequestAborted);
            if (platform is null) return Results.NotFound();

            var marketplace = platform is "TRENDYOL" or "SHOPIFY";
            var jobType = marketplace
                ? MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.ConnectionTest)
                : InvoicingJobTypes.ConnectionTest;
            var result = marketplace
                ? await marketplaceProcessor.ProcessAsync(tenant.TenantId, id, jobType, "{}", http.TraceIdentifier, http.RequestAborted)
                : await invoicingProcessor.ProcessAsync(tenant.TenantId, id, jobType, "{}", http.TraceIdentifier, http.RequestAborted);
            return Results.Ok(result);
        });
        api.MapPut("/connections/{id:guid}/active", async (Guid id, ActiveCommand command, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.SetActiveAsync(tenant.TenantId, id, version, command.Active, http.RequestAborted), x => x.Version) : failure! : MissingContext(http));
        api.MapPost("/connections/{id:guid}/deep-delete", async (Guid id, DeleteConnectionCommand command, HttpContext http, IOperationalDataMaintenanceService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.DeleteConnectionAsync(tenant.TenantId, tenant.UserId, id, version, command, http.TraceIdentifier, http.RequestAborted), Results.Ok) : failure! : MissingContext(http));
        api.MapPut("/connections/{id:guid}/data-visibility", async (Guid id, DataVisibilityCommand command, HttpContext http, IOperationalDataMaintenanceService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.SetDataVisibilityAsync(tenant.TenantId, tenant.UserId, id, version, command.Hidden, http.TraceIdentifier, http.RequestAborted), Results.Ok) : failure! : MissingContext(http));
        api.MapPost("/settings/data-reset", async (ResetOperationalDataCommand command, HttpContext http, IOperationalDataMaintenanceService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.ResetAsync(tenant.TenantId, tenant.UserId, command, http.TraceIdentifier, http.RequestAborted), Results.Ok) : MissingContext(http));
        api.MapGet("/connections/{id:guid}/capabilities", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? Result(await service.CapabilitiesAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPut("/connections/{id:guid}/capabilities/{code}/evidence", async (Guid id, string code, RecordCapabilityEvidenceCommand command, HttpContext http, IMarketplaceConnectionService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null
                ? TryIfMatch(http, out var version, out var failure)
                    ? WithEtag(http, await service.RecordCapabilityEvidenceAsync(tenant.TenantId, tenant.UserId, id, code, version, command, http.TraceIdentifier, http.RequestAborted), x => x.Version)
                    : failure!
                : MissingContext(http));
        api.MapGet("/connections/{id:guid}/sync-policies", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? Result(await service.SyncPoliciesAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPut("/connections/{id:guid}/sync-policies/{resourceType}", async (Guid id, string resourceType, UpdateSyncPolicyCommand command, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http); var expected = OptionalIfMatch(http, out var malformed); if (malformed is not null) return malformed;
            return Result(await service.UpsertSyncPolicyAsync(tenant.TenantId, id, resourceType, expected, command, http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Version}\""; return Results.Ok(value); });
        });
        api.MapGet("/connections/{id:guid}/webhooks", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? Result(await service.WebhooksAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPost("/connections/{id:guid}/webhooks", async (Guid id, CreateWebhookSubscriptionCommand command, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Created(await service.CreateWebhookAsync(tenant.TenantId, id, command, http.RequestAborted), $"/api/v1/connections/{id:D}/webhooks") : MissingContext(http));

        api.MapGet("/orders", async (HttpContext http, IMarketplaceSalesService service, int? limit, int? page, string? after, string? status, string? search, string? platform, string? platforms, string? listing, string? cargo, string? invoice, string? invoiceType, string? invoiceRegion, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? sort) =>
            Tenant(http) is { } tenant
                ? Results.Ok(await service.OrdersAsync(tenant.TenantId, PageSize(limit), PageNumber(page), after, new(status, search, platform, listing, cargo, invoice, invoiceType, invoiceRegion, dateFrom, dateTo, sort, platforms?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), http.RequestAborted))
                : Unauthorized(http));
        api.MapGet("/orders/summary", async (HttpContext http, IMarketplaceSalesService service, string? platform) => Tenant(http) is { } tenant ? Results.Ok(await service.OrderSummaryAsync(tenant.TenantId, platform, http.RequestAborted)) : Unauthorized(http));
        api.MapPost("/orders/shopify-csv-import", ImportShopifyOrdersAsync).DisableAntiforgery();
        api.MapGet("/orders/product-image", async (HttpContext http, IMarketplaceSalesService service, string? barcode) => Tenant(http) is { } tenant ? Result(await service.ProductImageAsync(tenant.TenantId, barcode, http.TraceIdentifier, http.RequestAborted), value => Results.Redirect(value)) : Unauthorized(http));
        api.MapGet("/orders/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.OrderAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPut("/orders/{id:guid}/shopify-status", UpdateShopifyOrderStatusAsync);
        api.MapPost("/orders/{id:guid}/instant-process", async (Guid id, HttpContext http, AppDbContext db, IMarketplaceSalesService service, IMarketplaceJobProcessor marketplaceProcessor) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } missingContext) return missingContext;

            var detail = await service.OrderAsync(tenant.TenantId, id, http.RequestAborted);
            if (!detail.Succeeded) return Problem(http, detail.Error!);
            var package = detail.Value!.Packages.FirstOrDefault();
            if (package is null)
            {
                if (detail.Value.ConnectionId is not { } connectionId)
                    return Problem(http, new("ORDER_CONNECTION_REQUIRED", "Siparişin aktif platform bağlantısı bulunamadı.", 422));

                // Refresh an order without creating a worker job, then continue the
                // picking action in the same request.
                var platform = await db.PlatformConnections.AsNoTracking()
                    .Where(x => x.TenantId == tenant.TenantId && x.Id == connectionId)
                    .Select(x => x.PlatformCode)
                    .SingleOrDefaultAsync(http.RequestAborted);
                var sync = await marketplaceProcessor.ProcessAsync(
                    tenant.TenantId,
                    connectionId,
                    MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.OrderSync),
                    JsonSerializer.Serialize(new { connectionId, externalOrderId = detail.Value.OrderNumber, full = false }),
                    OperationCorrelation(http),
                    http.RequestAborted);
                if (!sync.Succeeded)
                {
                    var status = sync.Kind == JobCompletionKind.Retry ? 503 : 422;
                    return Problem(http, new(sync.ErrorCode ?? "ORDER_REFRESH_FAILED", sync.ErrorSummary ?? (platform == "SHOPIFY" ? "Sipariş Shopify’dan anlık olarak yenilenemedi." : "Sipariş Trendyol’dan anlık olarak yenilenemedi."), status));
                }

                detail = await service.OrderAsync(tenant.TenantId, id, http.RequestAborted);
                if (!detail.Succeeded) return Problem(http, detail.Error!);
                package = detail.Value!.Packages.FirstOrDefault();
            }

            if (package is null)
                return Problem(http, new("PICKING_PACKAGE_NOT_FOUND", "Sipariş yenilendi ancak işleme alınabilir kargo paketi bulunamadı.", 422));

            return Result(await service.ProcessShipmentInstantAsync(
                tenant.TenantId,
                package.Id,
                package.Version,
                http.Request.Headers["Idempotency-Key"].ToString(),
                OperationCorrelation(http),
                http.RequestAborted), value =>
            {
                http.Response.Headers.ETag = $"\"v{value.Version}\"";
                return Results.Ok(value);
            });
        });
        api.MapPost("/connections/{connectionId:guid}/order-sync-jobs", async (Guid connectionId, OrderSyncCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueOrderSyncAsync(tenant.TenantId, connectionId, command.ExternalOrderId, command.Full, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/connections/{connectionId:guid}/product-sync-jobs", async (Guid connectionId, bool? full, bool? newOnly, bool? existingOnly, bool? mappingOnly, bool? includeArchived, bool? includeDrafts, bool? updateExistingProducts, string? lookup, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueProductSyncAsync(tenant.TenantId, connectionId, full == true, newOnly == true, existingOnly == true, mappingOnly == true, includeArchived == true, includeDrafts == true, updateExistingProducts != false, lookup, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/connections/{connectionId:guid}/stage-test-order-jobs", async (Guid connectionId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueStageTestOrderAsync(tenant.TenantId, tenant.UserId, connectionId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/connections/{connectionId:guid}/reference-sync-jobs", async (Guid connectionId, string? resourceType, string? parentExternalId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReferenceSyncAsync(tenant.TenantId, connectionId, resourceType ?? "CATEGORIES", parentExternalId, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapGet("/shipments", async (HttpContext http, IMarketplaceSalesService service, int? limit, string? after, string? status) => Tenant(http) is { } tenant ? Results.Ok(await service.ShipmentsAsync(tenant.TenantId, PageSize(limit), after, status, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/shipments/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.ShipmentAsync(tenant.TenantId, id, http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Package.Version}\""; return Results.Ok(value); }) : Unauthorized(http));
        api.MapPost("/shipments/{id:guid}/actions", async (Guid id, ShipmentActionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueShipmentActionAsync(tenant.TenantId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted)) : failure! : MissingContext(http));
        api.MapPost("/shipments/{id:guid}/instant-process", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.ProcessShipmentInstantAsync(tenant.TenantId, id, version, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Version}\""; return Results.Ok(value); }) : failure! : MissingContext(http));
        api.MapPost("/shipments/{id:guid}/instant-cargo-provider", async (Guid id, ShipmentActionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.ChangeCargoProviderInstantAsync(tenant.TenantId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Version}\""; return Results.Ok(value); }) : failure! : MissingContext(http));
        api.MapPost("/shipments/{id:guid}/common-label-jobs", async (Guid id, CommonLabelCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueCommonLabelAsync(tenant.TenantId, id, version, command.BoxQuantity, command.VolumetricHeight, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : failure! : MissingContext(http));
        api.MapPost("/shipments/{id:guid}/label-capability-probes", async (Guid id, LabelCapabilityProbeCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueLabelCapabilityProbeAsync(tenant.TenantId, tenant.UserId, id, version, command.CapabilityCode, command.BoxQuantity, command.VolumetricHeight, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : failure! : MissingContext(http));
        api.MapGet("/returns", async (HttpContext http, IMarketplaceSalesService service, int? limit, string? after, string? status, string? customer, string? orderNumber, string? claimCode, string? barcode, string? reason, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, bool? latest) => Tenant(http) is { } tenant ? Results.Ok(await service.ReturnsAsync(tenant.TenantId, PageSize(limit), after, new ReturnListQuery(status, customer, orderNumber, claimCode, barcode, reason, dateFrom, dateTo), latest == true, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/returns/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.ReturnAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapGet("/returns/{id:guid}/rejection-reasons", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.ReturnIssueReasonsAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPost("/connections/{connectionId:guid}/return-sync-jobs", async (Guid connectionId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReturnSyncAsync(tenant.TenantId, connectionId, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/returns/{id:guid}/received", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.MarkReturnReceivedAsync(tenant.TenantId, tenant.UserId, id, version, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), x => x.Version) : failure! : MissingContext(http));
        api.MapPost("/returns/{id:guid}/actions", async (Guid id, ReturnDecisionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.ProcessReturnActionInstantAsync(tenant.TenantId, tenant.UserId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), x => x.Version) : failure! : MissingContext(http));
        api.MapPost("/returns/{id:guid}/stock-dispositions", async (Guid id, ReturnDispositionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? WithEtag(http, await service.ApplyDispositionAsync(tenant.TenantId, tenant.UserId, id, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), x => x.Version) : MissingContext(http));
        api.MapPost("/files/return-evidence", UploadReturnEvidenceAsync).DisableAntiforgery();

        api.MapPost("/hooks/{connectionPublicId:guid}/{routeToken}", ReceiveWebhook).DisableAntiforgery().RequireRateLimiting("webhook");
        return endpoints;
    }

    private static async Task<IResult> UpdateShopifyOrderStatusAsync(Guid id, ShopifyOrderStatusCommand command, HttpContext http, AppDbContext db, TimeProvider timeProvider)
    {
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } idempotencyFailure) return idempotencyFailure;

        var status = command.Status.Trim().ToUpperInvariant();
        if (status is not ("NEW" or "PROCESSING" or "ON_HOLD" or "READY_TO_SHIP" or "SHIPPED" or "UNDELIVERED" or "DELIVERED" or "RETURN_IN_TRANSIT" or "RETURNED" or "CANCELLED"))
            return Problem(http, new("SHOPIFY_ORDER_STATUS_INVALID", "Shopify sipariş durumu geçersiz.", 422));

        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Id == id, http.RequestAborted);
        if (order is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Sipariş kaydı bulunamadı.", 404));

        var platform = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.Id == order.ConnectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(http.RequestAborted);
        if (platform != "SHOPIFY") return Problem(http, new("SHOPIFY_ORDER_ONLY", "Bu manuel durum akışı yalnız Shopify siparişleri için kullanılabilir.", 422));

        var now = timeProvider.GetUtcNow();
        order.DerivedStatus = status;
        order.UpdatedAt = now;
        order.Version++;
        db.OrderStatusHistory.Add(new OrderStatusHistory
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.TenantId,
            OrderId = order.Id,
            CanonicalStatus = status,
            RawStatus = $"MANUAL_SHOPIFY_STATUS:{status}",
            SourceEventId = $"manual:shopify-status:{now.UtcTicks}:{Guid.NewGuid():N}",
            OccurredAt = now,
            RecordedAt = now
        });
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenant.TenantId,
            ActorUserId = tenant.UserId,
            Action = "SHOPIFY_ORDER_STATUS_MANUAL",
            TargetType = "Order",
            TargetId = id.ToString("D"),
            Reason = status,
            CorrelationId = http.TraceIdentifier,
            CreatedAt = now
        });
        await db.SaveChangesAsync(http.RequestAborted);
        http.Response.Headers.ETag = $"\"v{order.Version}\"";
        return Results.Ok(new { id = order.Id, status, version = order.Version });
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

    private static async Task<IResult> UploadReturnEvidenceAsync(HttpContext http, AppDbContext db, IPrivateFileStorage storage, TimeProvider timeProvider)
    {
        const long maximumBytes = 10 * 1024 * 1024;
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (!http.Request.HasFormContentType) return Problem(http, new("EVIDENCE_FORM_REQUIRED", "multipart/form-data body gereklidir.", 400));
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length <= 0) return Problem(http, new("EVIDENCE_FILE_REQUIRED", "file alanında kanıt dosyası gereklidir.", 400));
        if (file.Length > maximumBytes) return Problem(http, new("EVIDENCE_TOO_LARGE", "İade kanıtı en fazla 10 MiB olabilir.", 413));

        await using var input = file.OpenReadStream();
        await using var buffer = new MemoryStream((int)file.Length);
        await input.CopyToAsync(buffer, http.RequestAborted);
        if (buffer.Length != file.Length || buffer.Length > maximumBytes) return Problem(http, new("EVIDENCE_SIZE_MISMATCH", "Dosya boyutu doğrulanamadı.", 422));
        var bytes = buffer.ToArray();
        var mimeType = DetectEvidenceMimeType(bytes);
        if (mimeType is null) return Problem(http, new("EVIDENCE_TYPE_UNSUPPORTED", "Yalnız gerçek PDF, JPEG veya PNG iade kanıtı kabul edilir.", 415));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var asset = await db.FileAssets.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.Classification == "RETURN_EVIDENCE" && x.Sha256 == hash && x.ArchivedAt == null && x.Status == "ACTIVE", http.RequestAborted);
        if (asset is null)
        {
            var id = Guid.CreateVersion7();
            var extension = mimeType switch { "application/pdf" => ".pdf", "image/jpeg" => ".jpg", _ => ".png" };
            buffer.Position = 0;
            var stored = await storage.SaveAsync(tenant.TenantId, $"{id:N}{extension}", mimeType, buffer, maximumBytes, http.RequestAborted);
            asset = new FileAsset { Id = id, TenantId = tenant.TenantId, Classification = "RETURN_EVIDENCE", RelativePath = stored, OriginalNameSafe = Path.GetFileName(file.FileName), MimeType = mimeType, SizeBytes = bytes.LongLength, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
            db.FileAssets.Add(asset);
            await db.SaveChangesAsync(http.RequestAborted);
        }
        return Results.Created($"/api/v1/files/return-evidence/{asset.Id:D}", new { asset.Id, asset.MimeType, asset.SizeBytes, asset.Sha256 });
    }

    private static string? DetectEvidenceMimeType(byte[] bytes) => bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8.ToArray()) ? "application/pdf"
        : bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }) ? "image/jpeg"
        : bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : null;

    internal static async Task<IResult> ReceiveWebhook(Guid connectionPublicId, string routeToken, HttpContext http, IMarketplaceWebhookService service)
    {
        var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = WebhookBodyReader.MaximumBytes;
        var bodyRead = await WebhookBodyReader.ReadAsync(http.Request.Body, http.Request.ContentLength, http.RequestAborted);
        if (bodyRead.TooLarge) return Problem(http, new("WEBHOOK_TOO_LARGE", "Webhook gövdesi kabul edilen üst sınırı aşıyor.", 413));

        var headers = http.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        using var body = bodyRead.Body!;
        var result = await service.ReceiveAsync(connectionPublicId, routeToken, body.Memory, headers, http.TraceIdentifier, http.RequestAborted);
        return result.Succeeded ? Results.Ok(new { accepted = true }) : Problem(http, result.Error!);
    }
    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    private static string OperationCorrelation(HttpContext http) =>
        Guid.TryParse(http.Request.Headers["X-Operation-ID"].ToString(), out var operationId)
            ? $"operation-{operationId:N}"
            : http.TraceIdentifier;
    private static int PageSize(int? limit) => limit is null ? 50 : limit is >= 1 and <= 200 ? limit.Value : throw new ArgumentException("limit 1-200 arasında olmalıdır.");
    private static int PageNumber(int? page) => page is null ? 1 : page is >= 1 and <= 100_000 ? page.Value : throw new ArgumentException("page 1-100000 arasında olmalıdır.");
    private static IResult? RequireIdempotency(HttpContext http) => string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"]) ? Problem(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400)) : http.Request.Headers["Idempotency-Key"].ToString().Length > 256 ? Problem(http, new("IDEMPOTENCY_KEY_INVALID", "Idempotency-Key en fazla 256 karakterdir.", 400)) : null;
    private static bool TryIfMatch(HttpContext http, out long version, out IResult? failure) { var parsed = OptionalIfMatch(http, out failure); version = parsed ?? 0; if (failure is null && parsed is null) failure = Problem(http, new("PRECONDITION_REQUIRED", "If-Match gereklidir.", 428)); return failure is null; }
    private static long? OptionalIfMatch(HttpContext http, out IResult? failure) { failure = null; var value = http.Request.Headers.IfMatch.ToString(); if (string.IsNullOrWhiteSpace(value)) return null; if (value.Length >= 4 && value.StartsWith("\"v", StringComparison.Ordinal) && value.EndsWith('"') && long.TryParse(value[2..^1], out var version) && version > 0) return version; failure = Problem(http, new("INVALID_ETAG", "If-Match güçlü ETag biçiminde olmalıdır: \"v{version}\".", 400)); return null; }
    private static IResult WithEtag<T>(HttpContext http, ServiceResult<T> result, Func<T, long> version) { if (!result.Succeeded) return Problem(http, result.Error!); http.Response.Headers.ETag = $"\"v{version(result.Value!)}\""; return Results.Ok(result.Value); }
    private static IResult Created<T>(ServiceResult<T> result, string collection) { if (!result.Succeeded) return Results.Json(ToProblem(result.Error!, null), statusCode: result.Error!.Status); var id = typeof(T).GetProperty("Id")?.GetValue(result.Value); return Results.Created(id is Guid guid ? $"{collection}/{guid:D}" : collection, result.Value); }
    private static IResult Accepted(ServiceResult<Guid> result) => result.Succeeded ? Results.Accepted($"/api/v1/jobs/{result.Value:D}", new { jobId = result.Value }) : Results.Json(ToProblem(result.Error!, null), statusCode: result.Error!.Status);
    private static IResult Result<T>(ServiceResult<T> result, Func<T, IResult> success) => result.Succeeded ? success(result.Value!) : Results.Json(ToProblem(result.Error!, null), statusCode: result.Error!.Status);
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult MissingContext(HttpContext http) => Tenant(http) is null ? Unauthorized(http) : RequireIdempotency(http) ?? Problem(http, new("REQUEST_INVALID", "İstek tamamlanamadı.", 400));
    private static IResult Problem(HttpContext http, ServiceError error) => Results.Json(ToProblem(error, http.TraceIdentifier), statusCode: error.Status, contentType: "application/problem+json");
    private static object ToProblem(ServiceError error, string? correlationId) => new { type = $"https://marketplacehub.invalid/problems/{error.Code.ToLowerInvariant().Replace('_', '-')}", title = error.Message, status = error.Status, code = error.Code, correlationId, retryable = error.Status is 429 or >= 500, fieldErrors = error.FieldErrors };
    public sealed record ActiveCommand(bool Active);
    public sealed record OrderSyncCommand(string? ExternalOrderId, bool Full = false);
    public sealed record CommonLabelCommand(int BoxQuantity, decimal VolumetricHeight);
    public sealed record LabelCapabilityProbeCommand(string CapabilityCode, int BoxQuantity, decimal VolumetricHeight);
}
