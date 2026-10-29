using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Api.Operations;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Marketplace;

public static class MarketplaceEndpoints
{
    public static IEndpointRouteBuilder MapMarketplaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter(async (context, next) => { try { return await next(context); } catch (ArgumentException exception) { var (code, message) = MarketplaceHub.Api.Security.RequestArgumentError.From(exception); return Problem(context.HttpContext, new(code, message, 400)); } });
        api.MapGet("/connections", async (HttpContext http, IMarketplaceConnectionService service, int? limit, string? after) => Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http));
        api.MapPost("/connections", async (CreateConnectionCommand command, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            var result = await service.CreateAsync(tenant.TenantId, command, http.RequestAborted);
            if (result is { Succeeded: true, Value: { } created }) await AuditAsync(http, tenant, "CONNECTION_CREATED", created.Id, $"{created.PlatformCode}:{created.Environment}");
            return Created(result, "/api/v1/connections");
        });
        api.MapGet("/connections/{id:guid}", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapPatch("/connections/{id:guid}", async (Guid id, UpdateConnectionCommand command, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (!TryIfMatch(http, out var version, out var failure)) return failure!;
            var result = await service.UpdateAsync(tenant.TenantId, id, version, command, http.RequestAborted, RoleAuthorizationMiddleware.IsElevated(tenant.Role));
            // The role refusal is the panel's own 403, not a marketplace's (which WithEtag reports as 502).
            return result.Error is { Code: "PERMISSION_DENIED" } denied ? Problem(http, denied) : WithEtag(http, result, x => x.Version);
        });
        api.MapPut("/connections/{id:guid}/credential", async (Guid id, CredentialCommand command, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.RotateCredentialAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure! : MissingContext(http));
        // Instant work below runs with the server's budget as http.RequestAborted, not the browser's connection.
        api.MapPost("/connections/{id:guid}/test-jobs", async (Guid id, HttpContext http, AppDbContext db, IMarketplaceJobProcessor marketplaceProcessor, IInvoicingJobProcessor invoicingProcessor) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            var platform = await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId && x.Id == id)
                .Select(x => x.PlatformCode)
                .SingleOrDefaultAsync(http.RequestAborted);
            if (platform is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Bağlantı bulunamadı.", 404));

            var (marketplace, jobType) = MarketplaceConnectionTestDispatchPolicy.Resolve(platform);
            var result = marketplace
                ? await marketplaceProcessor.ProcessAsync(tenant.TenantId, id, jobType, "{}", http.TraceIdentifier, http.RequestAborted)
                : await invoicingProcessor.ProcessAsync(tenant.TenantId, id, jobType, "{}", http.TraceIdentifier, http.RequestAborted);
            return Results.Ok(result);
        }).WithServerBudget();
        // n11 cargo template names from the seller panel, offered as choices for the product create setting.
        api.MapGet("/connections/{id:guid}/n11-shipment-templates", async (Guid id, HttpContext http, AppDbContext db, MarketplaceHub.Infrastructure.Adapters.N11.N11HttpClient n11) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var connection = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Id == id).Select(x => new { x.PlatformCode, x.Status }).SingleOrDefaultAsync(http.RequestAborted);
            if (connection is null || connection.PlatformCode != "N11") return Problem(http, new("RESOURCE_NOT_FOUND", "N11 bağlantısı bulunamadı.", 404));
            if (connection.Status is not ("ACTIVE" or "VERIFIED")) return Problem(http, new("N11_CONNECTION_INACTIVE", "Kargo şablonları yalnız aktif veya doğrulanmış N11 bağlantısından okunur.", 409));
            var result = await n11.ShipmentTemplatesAsync(tenant.TenantId, id, http.RequestAborted);
            return result.IsSuccess ? Results.Ok(result.Value) : Problem(http, MarketplaceErrorStatus.ToServiceError(result.Error));
        }).RequireSameOriginFetch();
        api.MapPut("/connections/{id:guid}/active", async (Guid id, ActiveCommand command, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.SetActiveAsync(tenant.TenantId, id, version, command.Active, http.RequestAborted), x => x.Version) : failure! : MissingContext(http));
        api.MapPost("/connections/{id:guid}/initial-data-sync-jobs", async (Guid id, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            if (!TryIfMatch(http, out var version, out var failure)) return failure!;
            return Accepted(await service.EnqueueInitialDataSyncAsync(tenant.TenantId, id, http.Request.Headers["Idempotency-Key"].ToString(), version, http.TraceIdentifier, http.RequestAborted));
        });
        api.MapPost("/connections/{id:guid}/deep-delete", async (Guid id, DeleteConnectionCommand command, HttpContext http, IOperationalDataMaintenanceService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.DeleteConnectionAsync(tenant.TenantId, tenant.UserId, id, version, command, http.TraceIdentifier, http.RequestAborted), Results.Ok) : failure! : MissingContext(http));
        api.MapGet("/connections/{id:guid}/data-reset", async (Guid id, HttpContext http, IOperationalDataMaintenanceService service) => Tenant(http) is { } tenant ? Result(await service.ResetCountsAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPost("/connections/{id:guid}/data-reset", async (Guid id, ResetOperationalDataCommand command, HttpContext http, IOperationalDataMaintenanceService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.ResetConnectionDataAsync(tenant.TenantId, tenant.UserId, id, version, command, http.TraceIdentifier, http.RequestAborted), Results.Ok) : failure! : MissingContext(http));
        api.MapPut("/connections/{id:guid}/data-visibility", async (Guid id, DataVisibilityCommand command, HttpContext http, IOperationalDataMaintenanceService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.SetDataVisibilityAsync(tenant.TenantId, tenant.UserId, id, version, command.Hidden, http.TraceIdentifier, http.RequestAborted), Results.Ok) : failure! : MissingContext(http));
        api.MapGet("/settings/data-reset", async (HttpContext http, IOperationalDataMaintenanceService service) => Tenant(http) is { } tenant ? Result(await service.ResetCountsAsync(tenant.TenantId, null, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPost("/settings/data-reset", async (ResetOperationalDataCommand command, HttpContext http, IOperationalDataMaintenanceService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.ResetAsync(tenant.TenantId, tenant.UserId, command, http.TraceIdentifier, http.RequestAborted), Results.Ok) : MissingContext(http));
        api.MapGet("/connections/{id:guid}/capabilities", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? Result(await service.CapabilitiesAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPut("/connections/{id:guid}/capabilities/{code}/evidence", async (Guid id, string code, RecordCapabilityEvidenceCommand command, HttpContext http, IMarketplaceConnectionService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null
                ? TryIfMatch(http, out var version, out var failure)
                    ? WithEtag(http, await service.RecordCapabilityEvidenceAsync(tenant.TenantId, tenant.UserId, id, code, version, command, http.TraceIdentifier, http.RequestAborted), x => x.Version)
                    : failure!
                : MissingContext(http));
        api.MapPost("/connections/{id:guid}/sync-policies/{resourceType}/runs", async (Guid id, string resourceType, HttpContext http, IScheduledJobProducer producer) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null
                ? Accepted(await producer.RunNowAsync(tenant.TenantId, id, resourceType, OperationCorrelation(http), http.RequestAborted))
                : MissingContext(http));
        api.MapGet("/connections/{id:guid}/sync-policies", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? Result(await service.SyncPoliciesAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPut("/connections/{id:guid}/sync-policies/{resourceType}", async (Guid id, string resourceType, UpdateSyncPolicyCommand command, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http); var expected = OptionalIfMatch(http, out var malformed); if (malformed is not null) return malformed;
            return Result(await service.UpsertSyncPolicyAsync(tenant.TenantId, id, resourceType, expected, command, http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Version}\""; return Results.Ok(value); });
        });
        api.MapGet("/connections/{id:guid}/webhooks", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? Result(await service.WebhooksAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPost("/connections/{id:guid}/webhooks", async (Guid id, CreateWebhookSubscriptionCommand command, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            var result = await service.CreateWebhookAsync(tenant.TenantId, id, command, http.RequestAborted);
            if (result is { Succeeded: true, Value: { } created }) await AuditAsync(http, tenant, "WEBHOOK_SUBSCRIPTION_CREATED", id, $"subscription:{created.Subscription.Id:D};{created.Subscription.AuthenticationType}");
            return Created(result, $"/api/v1/connections/{id:D}/webhooks");
        });
        api.MapDelete("/connections/{id:guid}/webhooks/{subscriptionId:guid}", async (Guid id, Guid subscriptionId, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            var result = await service.RevokeWebhookAsync(tenant.TenantId, id, subscriptionId, http.RequestAborted);
            if (result.Succeeded) await AuditAsync(http, tenant, "WEBHOOK_SUBSCRIPTION_REVOKED", id, $"subscription:{subscriptionId:D}");
            return Result(result, Results.Ok);
        });

        api.MapGet("/orders", async (HttpContext http, IMarketplaceSalesService service, int? limit, int? page, string? after, string? status, string? search, string? platform, string? platforms, string? listing, string? cargo, string? invoice, string? invoiceType, string? invoiceRegion, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? sort, bool? labelPrinted) =>
            Tenant(http) is { } tenant
                ? Results.Ok(await service.OrdersAsync(tenant.TenantId, PageSize(limit), PageNumber(page), after, OrderQuery(status, search, platform, platforms, listing, cargo, invoice, invoiceType, invoiceRegion, dateFrom, dateTo, sort, labelPrinted), http.RequestAborted))
                : Unauthorized(http));
        // Records that a shipping label of the order was printed, so every computer sees it.
        api.MapPost("/orders/{id:guid}/label-printed", async (Guid id, HttpContext http, IMarketplaceSalesService service) =>
            Tenant(http) is { } tenant
                ? await service.MarkLabelPrintedAsync(tenant.TenantId, id, http.RequestAborted) ? Results.NoContent() : Results.NotFound()
                : Unauthorized(http));
        // Tab counters for the same filters as GET /orders (the status tab itself is ignored).
        api.MapGet("/orders/summary", async (HttpContext http, IMarketplaceSalesService service, string? search, string? platform, string? platforms, string? listing, string? cargo, string? invoice, string? invoiceType, string? invoiceRegion, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, bool? labelPrinted, string? tabs) =>
            Tenant(http) is { } tenant
                // Every caller shares an identical answer while no order, package, claim or store of the tenant changed.
                ? Results.Ok(await service.OrderSummaryAsync(tenant.TenantId, OrderQuery(null, search, platform, platforms, listing, cargo, invoice, invoiceType, invoiceRegion, dateFrom, dateTo, null, labelPrinted), http.RequestAborted,
                    string.IsNullOrWhiteSpace(tabs) ? null : tabs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    allowCached: true))
                : Unauthorized(http));
        api.MapPost("/orders/shopify-csv-import", ImportShopifyOrdersAsync).DisableAntiforgery();
        api.MapGet("/orders/product-image", async (HttpContext http, IMarketplaceSalesService service, string? barcode, Guid? connectionId, string? productName) => Tenant(http) is { } tenant ? Result(await service.ProductImageAsync(tenant.TenantId, barcode, http.TraceIdentifier, http.RequestAborted, connectionId, productName), value => Results.Redirect(value)) : Unauthorized(http)).RequireSameOriginFetch();
        api.MapGet("/orders/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.OrderAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapGet("/orders/{id:guid}/finance", async (Guid id, HttpContext http, AppDbContext db, IOrderFinancePort finance, TimeProvider clock) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var order = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Id == id).Select(x => new { x.ConnectionId, x.OrderNumber, x.OrderedAt }).SingleOrDefaultAsync(http.RequestAborted);
            if (order is null) return Problem(http, new("RESOURCE_NOT_FOUND", "Sipariş bulunamadı.", 404));
            var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Id == order.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(http.RequestAborted);
            if (platform != "PAZARAMA") return Problem(http, new("ORDER_FINANCE_UNSUPPORTED", "Finans bilgisi yalnız Pazarama siparişlerinde okunur.", 422));
            var now = clock.GetUtcNow();
            var context = new AdapterContext(tenant.TenantId, order.ConnectionId, http.TraceIdentifier, $"order-finance:{id:N}", now.AddMinutes(1), Operation: IntegrationOperation.Manual);
            var result = await finance.GetOrderFinanceAsync(context, order.OrderNumber, order.OrderedAt.AddDays(-1), now.AddDays(1), http.RequestAborted);
            return result.IsSuccess
                ? Results.Ok(new { readAt = now, transactions = result.Value!.Transactions, commissionInvoices = result.Value.CommissionInvoices })
                : Problem(http, MarketplaceErrorStatus.ToServiceError(result.Error));
        }).RequireSameOriginFetch();
        api.MapPut("/orders/{id:guid}/shopify-status", UpdateShopifyOrderStatusAsync);
        api.MapPost("/orders/{id:guid}/instant-process", async (Guid id, HttpContext http, AppDbContext db, IMarketplaceSalesService service, IMarketplaceJobProcessor marketplaceProcessor) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } missingContext) return missingContext;
            return await InstantProcessOrderAsync(id, tenant, http, db, service, marketplaceProcessor, http.RequestAborted);
        }).WithServerBudget();
        api.MapPost("/connections/{connectionId:guid}/order-sync-jobs", async (Guid connectionId, OrderSyncCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueOrderSyncAsync(tenant.TenantId, connectionId, command.ExternalOrderId, command.Full, http.TraceIdentifier, http.RequestAborted, command.PackageNumber)) : MissingContext(http));
        api.MapPost("/connections/{connectionId:guid}/product-sync-jobs", async (Guid connectionId, bool? full, bool? newOnly, bool? existingOnly, bool? mappingOnly, bool? optionsOnly, bool? includeArchived, bool? includeDrafts, bool? includePendingApproval, bool? updateExistingProducts, bool? updateExistingPricesAndStock, string? lookup, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueProductSyncAsync(tenant.TenantId, connectionId, full == true, newOnly == true, existingOnly == true, mappingOnly == true, optionsOnly == true, includeArchived == true, includeDrafts == true, includePendingApproval == true, updateExistingProducts != false, updateExistingPricesAndStock == true, lookup, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/connections/{connectionId:guid}/stage-test-order-jobs", async (Guid connectionId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueStageTestOrderAsync(tenant.TenantId, tenant.UserId, connectionId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/connections/{connectionId:guid}/catalog-structure-runs", async (Guid connectionId, string? mode, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.EnqueueCatalogStructureAsync(tenant.TenantId, connectionId, mode ?? "PREVIEW", http.TraceIdentifier, http.RequestAborted), value => Results.Accepted($"/api/v1/catalog-structure-runs/{value.Id:D}", value)) : MissingContext(http));
        api.MapGet("/connections/{connectionId:guid}/catalog-structure-runs", async (Guid connectionId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Results.Ok(await service.CatalogStructureRunsAsync(tenant.TenantId, connectionId, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/catalog-structure-runs/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.CatalogStructureRunAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapPost("/connections/{connectionId:guid}/reference-sync-jobs", async (Guid connectionId, string? resourceType, string? parentExternalId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReferenceSyncAsync(tenant.TenantId, connectionId, resourceType ?? "CATEGORIES", parentExternalId, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/orders/{id:guid}/packages", async (Guid id, OrderPackageCreateRequest command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.CreateOrderPackageInstantAsync(tenant.TenantId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), Results.Ok) : failure! : MissingContext(http)).WithServerBudget();
        api.MapGet("/shipments", async (HttpContext http, IMarketplaceSalesService service, int? limit, string? after, string? status) => Tenant(http) is { } tenant ? Results.Ok(await service.ShipmentsAsync(tenant.TenantId, PageSize(limit), after, status, http.RequestAborted)) : Unauthorized(http));
        api.MapGet("/shipments/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.ShipmentAsync(tenant.TenantId, id, http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Package.Version}\""; return Results.Ok(value); }) : Unauthorized(http));
        api.MapGet("/shipments/{id:guid}/changeable-cargo-companies", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.ChangeableCargoCompaniesAsync(tenant.TenantId, id, OperationCorrelation(http), http.RequestAborted), Results.Ok) : Unauthorized(http)).RequireSameOriginFetch();
        api.MapGet("/shipments/{id:guid}/documents/{documentId:guid}/content", async (Guid id, Guid documentId, HttpContext http, AppDbContext db, IPrivateFileStorage storage) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var asset = await (from document in db.ShipmentDocuments.AsNoTracking()
                               join file in db.FileAssets.AsNoTracking() on new { document.TenantId, FileAssetId = document.FileAssetId } equals new { file.TenantId, FileAssetId = file.Id }
                               where document.TenantId == tenant.TenantId && document.PackageId == id && document.Id == documentId && file.Status == "ACTIVE"
                               select file).SingleOrDefaultAsync(http.RequestAborted);
            if (asset is null) return Problem(http, new("SHIPMENT_DOCUMENT_NOT_FOUND", "Gönderi belgesi bulunamadı.", 404));
            var content = await storage.OpenReadAsync(tenant.TenantId, asset.RelativePath, http.RequestAborted);
            return Results.File(content, asset.MimeType, asset.OriginalNameSafe ?? "shipment-label.zpl", enableRangeProcessing: true);
        });
        api.MapPost("/shipments/{id:guid}/actions", async (Guid id, ShipmentActionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueShipmentActionAsync(tenant.TenantId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted)) : failure! : MissingContext(http));
        api.MapPost("/shipments/{id:guid}/instant-process", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.ProcessShipmentInstantAsync(tenant.TenantId, id, version, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Version}\""; return Results.Ok(value); }) : failure! : MissingContext(http)).WithServerBudget();
        api.MapPost("/shipments/{id:guid}/instant-cargo-provider", async (Guid id, ShipmentActionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.ChangeCargoProviderInstantAsync(tenant.TenantId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Version}\""; return Results.Ok(value); }) : failure! : MissingContext(http)).WithServerBudget();
        api.MapPost("/shipments/{id:guid}/common-label-jobs", async (Guid id, CommonLabelCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueCommonLabelAsync(tenant.TenantId, id, version, command.BoxQuantity, command.VolumetricHeight, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted, command.Format)) : failure! : MissingContext(http));
        api.MapPost("/shipments/{id:guid}/label-capability-probes", async (Guid id, LabelCapabilityProbeCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueLabelCapabilityProbeAsync(tenant.TenantId, tenant.UserId, id, version, command.CapabilityCode, command.BoxQuantity, command.VolumetricHeight, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : failure! : MissingContext(http));
        api.MapGet("/returns", async (HttpContext http, IMarketplaceSalesService service, int? limit, int? page, string? after, string? status, string? customer, string? orderNumber, string? claimCode, string? barcode, string? reason, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, bool? latest, string? search, string? platforms, string? cargo, string? reasonKey, string? invoiceStatus, string? sort) => Tenant(http) is { } tenant ? Results.Ok(await service.ReturnsAsync(tenant.TenantId, PageSize(limit), after, ReturnQuery(status, customer, orderNumber, claimCode, barcode, reason, dateFrom, dateTo, search, platforms, cargo, reasonKey, invoiceStatus, sort), latest == true, http.RequestAborted, PageNumber(page))) : Unauthorized(http));
        // Tab counts and filter choices of the Returns page, for the same filters as GET /returns.
        api.MapGet("/returns/summary", async (HttpContext http, IMarketplaceSalesService service, string? status, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? search, string? platforms, string? cargo, string? reasonKey, string? invoiceStatus) => Tenant(http) is { } tenant ? Results.Ok(await service.ReturnSummaryAsync(tenant.TenantId, ReturnQuery(status, null, null, null, null, null, dateFrom, dateTo, search, platforms, cargo, reasonKey, invoiceStatus, null), http.RequestAborted)) : Unauthorized(http));
        // Reads the return's status history from Trendyol live (getClaimItemAudits); nothing is stored or written.
        api.MapGet("/returns/{id:guid}/audits", async (Guid id, HttpContext http, IReturnAuditReader reader) => Tenant(http) is { } tenant ? Result(await reader.ReturnAuditsAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted), Results.Ok) : Unauthorized(http));
        api.MapGet("/returns/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.ReturnAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http));
        api.MapGet("/returns/{id:guid}/rejection-reasons", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.ReturnIssueReasonsAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted), Results.Ok) : Unauthorized(http)).RequireSameOriginFetch();
        api.MapPost("/connections/{connectionId:guid}/return-sync-jobs", async (Guid connectionId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReturnSyncAsync(tenant.TenantId, connectionId, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http));
        api.MapPost("/returns/{id:guid}/received", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.MarkReturnReceivedAsync(tenant.TenantId, tenant.UserId, id, version, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), x => x.Version) : failure! : MissingContext(http)).WithServerBudget();
        api.MapPost("/returns/{id:guid}/actions", async (Guid id, ReturnDecisionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.ProcessReturnActionInstantAsync(tenant.TenantId, tenant.UserId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), x => x.Version) : failure! : MissingContext(http)).WithServerBudget();
        api.MapPost("/returns/{id:guid}/stock-dispositions", async (Guid id, ReturnDispositionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? WithEtag(http, await service.ApplyDispositionAsync(tenant.TenantId, tenant.UserId, id, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), x => x.Version) : MissingContext(http));
        api.MapPost("/files/return-evidence", UploadReturnEvidenceAsync).DisableAntiforgery();

        api.MapGet("/connections/{id:guid}/instant-notifications", async (Guid id, HttpContext http, IInstantNotificationService service) => Tenant(http) is { } tenant ? Result(await service.StatusAsync(tenant.TenantId, id, OperationCorrelation(http), http.RequestAborted), Results.Ok) : Unauthorized(http)).RequireSameOriginFetch();
        api.MapPost("/connections/{id:guid}/instant-notifications", async (Guid id, InstantNotificationCommand? command, HttpContext http, IInstantNotificationService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            // Hepsiburada: the previous definition (the one Hepsiburada was given) is made the active one again.
            // Or the definition an inactive Hepsiburada connection (a test store) was given is moved here.
            var restore = command?.RestorePrevious == true;
            var adopt = command?.AdoptFromInactive == true;
            var result = adopt
                ? await service.AdoptFromInactiveAsync(tenant.TenantId, id, OperationCorrelation(http), http.RequestAborted)
                : restore
                ? await service.RestorePreviousAsync(tenant.TenantId, id, OperationCorrelation(http), http.RequestAborted)
                : await service.EnableAsync(tenant.TenantId, id, OperationCorrelation(http), http.RequestAborted, command?.ClientSecret);
            if (result is { Succeeded: true, Value: { } view }) await AuditAsync(http, tenant, adopt ? "INSTANT_NOTIFICATIONS_ADOPTED" : restore ? "INSTANT_NOTIFICATIONS_RESTORED" : "INSTANT_NOTIFICATIONS_ENABLED", id, view.State);
            return Result(result, Results.Ok);
        }).WithServerBudget();
        api.MapDelete("/connections/{id:guid}/instant-notifications", async (Guid id, HttpContext http, IInstantNotificationService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.DisableAsync(tenant.TenantId, id, OperationCorrelation(http), http.RequestAborted), Results.Ok) : MissingContext(http)).WithServerBudget();
        // Hepsiburada sends every event kind to its own path below the base address, some with PUT.
        api.MapMethods("/hooks/{connectionPublicId:guid}/{routeToken}", ["POST", "PUT"], ReceiveWebhook).DisableAntiforgery();
        api.MapMethods("/hooks/{connectionPublicId:guid}/{routeToken}/{**eventPath}", ["POST", "PUT"], ReceiveWebhookEvent).DisableAntiforgery();
        return endpoints;
    }

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

    private static async Task<IResult> UploadReturnEvidenceAsync(HttpContext http, AppDbContext db, IPrivateFileStorage storage, TimeProvider timeProvider)
    {
        const long maximumBytes = 10 * 1024 * 1024;
        if (Tenant(http) is not { } tenant) return Unauthorized(http);
        if (RequireIdempotency(http) is { } missingKey) return missingKey;
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

    private static async Task<IResult> InstantProcessOrderAsync(Guid id, TenantContext tenant, HttpContext http, AppDbContext db, IMarketplaceSalesService service, IMarketplaceJobProcessor marketplaceProcessor, CancellationToken cancellationToken)
    {
        var detail = await service.OrderAsync(tenant.TenantId, id, cancellationToken);
        if (!detail.Succeeded) return Problem(http, detail.Error!);
        if (detail.Value!.Packages.Any(package => package.Status == "PAYMENT_PENDING"))
            return Problem(http, new("SHIPMENT_PAYMENT_PENDING", "Trendyol ödeme onayı vermeden sipariş işleme alınamaz.", 409));
        var package = PickingTarget(detail.Value!.Packages);
        if (package is null)
        {
            if (detail.Value.ConnectionId is not { } connectionId)
                return Problem(http, new("ORDER_CONNECTION_REQUIRED", "Siparişin aktif platform bağlantısı bulunamadı.", 422));

            // Refresh an order without creating a worker job, then continue the
            // picking action in the same request.
            var platform = await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId && x.Id == connectionId)
                .Select(x => x.PlatformCode)
                .SingleOrDefaultAsync(cancellationToken);
            var sync = await marketplaceProcessor.ProcessAsync(
                tenant.TenantId,
                connectionId,
                MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.OrderSync),
                JsonSerializer.Serialize(new { connectionId, externalOrderId = detail.Value.OrderNumber, full = false }),
                OperationCorrelation(http),
                cancellationToken);
            if (!sync.Succeeded)
            {
                var status = sync.Kind == JobCompletionKind.Retry ? 503 : 422;
                return Problem(http, new(sync.ErrorCode ?? "ORDER_REFRESH_FAILED", sync.ErrorSummary ?? (platform == "SHOPIFY" ? "Sipariş Shopify’dan anlık olarak yenilenemedi." : "Sipariş Trendyol’dan anlık olarak yenilenemedi."), status));
            }

            detail = await service.OrderAsync(tenant.TenantId, id, cancellationToken);
            if (!detail.Succeeded) return Problem(http, detail.Error!);
            if (detail.Value!.Packages.Any(package => package.Status == "PAYMENT_PENDING"))
                return Problem(http, new("SHIPMENT_PAYMENT_PENDING", "Trendyol ödeme onayı vermeden sipariş işleme alınamaz.", 409));
            package = PickingTarget(detail.Value!.Packages);
        }

        if (package is null)
            return Problem(http, new("PICKING_PACKAGE_NOT_FOUND", "Sipariş yenilendi ancak işleme alınabilir kargo paketi bulunamadı.", 422));

        return Result(await service.ProcessShipmentInstantAsync(
            tenant.TenantId,
            package.Id,
            package.Version,
            http.Request.Headers["Idempotency-Key"].ToString(),
            OperationCorrelation(http),
            cancellationToken), value =>
        {
            http.Response.Headers.ETag = $"\"v{value.Version}\"";
            return Results.Ok(value);
        });
    }

    // The order-level "İşleme Al" works on the package that is still new (a split order may hold
    // shipped or cancelled packages first); without one, the first package answers why not.
    internal static ShipmentView? PickingTarget(IReadOnlyList<ShipmentView> packages) =>
        packages.FirstOrDefault(package => package.Status == "NEW") ?? packages.FirstOrDefault();

    internal static Task<IResult> ReceiveWebhookEvent(Guid connectionPublicId, string routeToken, string? eventPath, HttpContext http, IMarketplaceWebhookService service, IWebhookRouteGate gate) =>
        ReceiveWebhookCore(connectionPublicId, routeToken, http, service, gate, eventPath);

    internal static async Task<IResult> ReceiveWebhook(Guid connectionPublicId, string routeToken, HttpContext http, IMarketplaceWebhookService service, IWebhookRouteGate gate) =>
        await ReceiveWebhookCore(connectionPublicId, routeToken, http, service, gate, null);

    private static async Task<IResult> ReceiveWebhookCore(Guid connectionPublicId, string routeToken, HttpContext http, IMarketplaceWebhookService service, IWebhookRouteGate gate, string? eventPath)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // The address (connection and route token) is checked before anything is read: an unknown one costs no body buffering.
        if (!await gate.IsKnownAsync(connectionPublicId, routeToken, http.RequestAborted))
        {
            // A known connection called with an old or wrong address is shown on its webhook panel; the body stays unread.
            await service.NoteRouteRefusedAsync(connectionPublicId, http.RequestAborted);
            return Problem(http, new("WEBHOOK_ROUTE_NOT_FOUND", "Webhook route bulunamadı.", 404));
        }
        var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = WebhookBodyReader.MaximumBytes;
        var bodyRead = await WebhookBodyReader.ReadAsync(http.Request.Body, http.Request.ContentLength, http.RequestAborted);
        if (bodyRead.TooLarge) return Problem(http, new("WEBHOOK_TOO_LARGE", "Webhook gövdesi kabul edilen üst sınırı aşıyor.", 413));

        var headers = http.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        // The event path comes from the route only; a sender cannot set it through a header.
        headers.Remove(WebhookEventPath.Header);
        if (WebhookEventPath.Normalize(eventPath) is { } path) headers[WebhookEventPath.Header] = path;
        using var body = bodyRead.Body!;
        var result = await service.ReceiveAsync(connectionPublicId, routeToken, body.Memory, headers, http.TraceIdentifier, http.RequestAborted);
        // Hepsiburada blacklists a feed after 20 replies slower than five seconds in an hour.
        if (stopwatch.Elapsed > TimeSpan.FromSeconds(4))
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MarketplaceHub.Webhooks")
                .LogWarning("Webhook reply took {ElapsedMs} ms (connection {ConnectionPublicId}, path {EventPath}).", (long)stopwatch.Elapsed.TotalMilliseconds, connectionPublicId, eventPath ?? "/");
        if (!result.Succeeded)
        {
            // A refused delivery says why, with the body's outline only (its top-level names), never its content.
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MarketplaceHub.Webhooks")
                .LogWarning("Webhook refused with {Code} ({Status}) for connection {ConnectionPublicId}, path {EventPath}; body outline {Outline}.",
                    result.Error!.Code, result.Error.Status, connectionPublicId, eventPath ?? "/", WebhookBodyOutline(body.Memory));
            return Problem(http, result.Error!);
        }
        if (!result.Value)
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MarketplaceHub.Webhooks")
                .LogWarning("Webhook acknowledged but not processed (stale delivery) for connection {ConnectionPublicId}.", connectionPublicId);
        // Hepsiburada's event paths answer as its contracts show: a POST (orders, packages, claims/packages) 201 Created,
        // a PUT 204; a repeated delivery gets the same answer. The base address keeps its body.
        if (eventPath is null) return Results.Ok(new { accepted = true });
        return HttpMethods.IsPost(http.Request.Method) ? Results.StatusCode(StatusCodes.Status201Created) : Results.NoContent();
    }
    internal static string WebhookBodyOutline(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return $"{root.ValueKind} ({body.Length} bytes)";
            return string.Join(',', root.EnumerateObject().Select(property => $"{property.Name}:{property.Value.ValueKind}").Take(20)) + $" ({body.Length} bytes)";
        }
        catch (System.Text.Json.JsonException) { return $"not JSON ({body.Length} bytes)"; }
    }
    private static TenantContext? Tenant(HttpContext http) => http.RequestServices.GetRequiredService<ITenantContextAccessor>().Current;
    // A connection or a webhook subscription outlives the session that made it (G2): the audit log keeps who set it up.
    private static async Task AuditAsync(HttpContext http, TenantContext tenant, string action, Guid connectionId, string reason)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        db.AuditLogs.Add(new AuditLog { TenantId = tenant.TenantId, ActorUserId = tenant.UserId, Action = action, TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = reason, CorrelationId = http.TraceIdentifier, CreatedAt = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow() });
        await db.SaveChangesAsync(http.RequestAborted);
    }
    private static string OperationCorrelation(HttpContext http) =>
        Guid.TryParse(http.Request.Headers["X-Operation-ID"].ToString(), out var operationId)
            ? $"operation-{operationId:N}"
            : http.TraceIdentifier;
    private static int PageSize(int? limit) => limit is null ? 50 : limit is >= 1 and <= 200 ? limit.Value : throw new ArgumentException("limit 1-200 arasında olmalıdır.");
    private static ReturnListQuery ReturnQuery(string? status, string? customer, string? orderNumber, string? claimCode, string? barcode, string? reason, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? search, string? platforms, string? cargo, string? reasonKey, string? invoiceStatus, string? sort) =>
        new(status, customer, orderNumber, claimCode, barcode, reason, dateFrom, dateTo, search,
            platforms?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), cargo, reasonKey, invoiceStatus, sort);
    private static OrderListQuery OrderQuery(string? status, string? search, string? platform, string? platforms, string? listing, string? cargo, string? invoice, string? invoiceType, string? invoiceRegion, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? sort, bool? labelPrinted) =>
        new(status, search, platform, listing, cargo, invoice, invoiceType, invoiceRegion, dateFrom, dateTo, sort, platforms?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), labelPrinted);
    private static int PageNumber(int? page) => page is null ? 1 : page is >= 1 and <= 100_000 ? page.Value : throw new ArgumentException("page 1-100000 arasında olmalıdır.");
    private static IResult? RequireIdempotency(HttpContext http) => string.IsNullOrWhiteSpace(http.Request.Headers["Idempotency-Key"]) ? Problem(http, new("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı zorunludur.", 400)) : http.Request.Headers["Idempotency-Key"].ToString().Length > 256 ? Problem(http, new("IDEMPOTENCY_KEY_INVALID", "Idempotency-Key en fazla 256 karakterdir.", 400)) : null;
    private static bool TryIfMatch(HttpContext http, out long version, out IResult? failure) { var parsed = OptionalIfMatch(http, out failure); version = parsed ?? 0; if (failure is null && parsed is null) failure = Problem(http, new("PRECONDITION_REQUIRED", "If-Match gereklidir.", 428)); return failure is null; }
    private static long? OptionalIfMatch(HttpContext http, out IResult? failure) { failure = null; var value = http.Request.Headers.IfMatch.ToString(); if (string.IsNullOrWhiteSpace(value)) return null; if (value.Length >= 4 && value.StartsWith("\"v", StringComparison.Ordinal) && value.EndsWith('"') && long.TryParse(value[2..^1], out var version) && version > 0) return version; failure = Problem(http, new("INVALID_ETAG", "If-Match güçlü ETag biçiminde olmalıdır: \"v{version}\".", 400)); return null; }
    private static IResult WithEtag<T>(HttpContext http, ServiceResult<T> result, Func<T, long> version) { if (!result.Succeeded) return Problem(http, MarketplaceErrorStatus.ForPanel(result.Error!)); http.Response.Headers.ETag = $"\"v{version(result.Value!)}\""; return Results.Ok(result.Value); }
    private static IResult Created<T>(ServiceResult<T> result, string collection) { if (!result.Succeeded) return MarketplaceHub.Api.Security.ApiProblems.Of(null, MarketplaceErrorStatus.ForPanel(result.Error!)); var id = typeof(T).GetProperty("Id")?.GetValue(result.Value); return Results.Created(id is Guid guid ? $"{collection}/{guid:D}" : collection, result.Value); }
    private static IResult Accepted(ServiceResult<Guid> result) => result.Succeeded ? Results.Accepted($"/api/v1/jobs/{result.Value:D}", new { jobId = result.Value }) : MarketplaceHub.Api.Security.ApiProblems.Of(null, MarketplaceErrorStatus.ForPanel(result.Error!));
    private static IResult Result<T>(ServiceResult<T> result, Func<T, IResult> success) => result.Succeeded ? success(result.Value!) : MarketplaceHub.Api.Security.ApiProblems.Of(null, MarketplaceErrorStatus.ForPanel(result.Error!));
    private static IResult Unauthorized(HttpContext http) => Problem(http, new("AUTHENTICATION_REQUIRED", "Aktif tenant oturumu gereklidir.", 401));
    private static IResult MissingContext(HttpContext http) => Tenant(http) is null ? Unauthorized(http) : RequireIdempotency(http) ?? Problem(http, new("REQUEST_INVALID", "İstek tamamlanamadı.", 400));
    private static IResult Problem(HttpContext http, ServiceError error) => MarketplaceHub.Api.Security.ApiProblems.Of(http, error);
    public sealed record ActiveCommand(bool Active);
    public sealed record InstantNotificationCommand(string? ClientSecret, bool RestorePrevious = false, bool AdoptFromInactive = false);
    public sealed record OrderSyncCommand(string? ExternalOrderId, bool Full = false, string? PackageNumber = null);
    public sealed record CommonLabelCommand(int BoxQuantity, decimal VolumetricHeight, string Format = "ZPL");
    public sealed record LabelCapabilityProbeCommand(string CapabilityCode, int BoxQuantity, decimal VolumetricHeight);
}
