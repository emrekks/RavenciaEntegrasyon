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
    public static IEndpointRouteBuilder MapMarketplaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").WithArgumentErrorsAnswered().WithGates(ApiGate.SignedIn, ApiGate.Operations);
        api.MapGet("/connections", async (HttpContext http, IMarketplaceConnectionService service, int? limit, string? after) => Tenant(http) is { } tenant ? Results.Ok(await service.ListAsync(tenant.TenantId, PageSize(limit), after, http.RequestAborted)) : Unauthorized(http)).Produces<PageResult<ConnectionView>>();
        api.MapPost("/connections", async (CreateConnectionCommand command, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            var result = await service.CreateAsync(tenant.TenantId, command, http.RequestAborted);
            if (result is { Succeeded: true, Value: { } created }) await AuditAsync(http, tenant, "CONNECTION_CREATED", created.Id, $"{created.PlatformCode}:{created.Environment}");
            return Created(result, "/api/v1/connections");
        }).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<ConnectionView>(StatusCodes.Status201Created);
        api.MapGet("/connections/{id:guid}", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.GetAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http)).Produces<ConnectionView>();
        api.MapPatch("/connections/{id:guid}", async (Guid id, UpdateConnectionCommand command, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (!TryIfMatch(http, out var version, out var failure)) return failure!;
            var result = await service.UpdateAsync(tenant.TenantId, id, version, command, http.RequestAborted, RoleAuthorizationMiddleware.IsElevated(tenant.Role));
            // The role refusal is the panel's own 403, not a marketplace's (which WithEtag reports as 502).
            return result.Error is { Code: "PERMISSION_DENIED" } denied ? Problem(http, denied) : WithEtag(http, result, x => x.Version);
        }).Produces<ConnectionView>().IfMatch().Idempotent(WriteReasons.VersionGuarded);
        api.MapPut("/connections/{id:guid}/credential", async (Guid id, CredentialCommand command, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.RotateCredentialAsync(tenant.TenantId, id, version, command, http.RequestAborted), x => x.Version) : failure! : MissingContext(http)).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<ConnectionView>().IfMatch();
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
        }).WithServerBudget().RequireIdempotencyKey().Produces<JobExecutionResult>();
        // n11 cargo template names from the seller panel, offered as choices for the product create setting.
        api.MapGet("/connections/{id:guid}/n11-shipment-templates", async (Guid id, HttpContext http, AppDbContext db, MarketplaceHub.Infrastructure.Adapters.N11.N11HttpClient n11) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var connection = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.Id == id).Select(x => new { x.PlatformCode, x.Status }).SingleOrDefaultAsync(http.RequestAborted);
            if (connection is null || connection.PlatformCode != "N11") return Problem(http, new("RESOURCE_NOT_FOUND", "N11 bağlantısı bulunamadı.", 404));
            if (connection.Status is not ("ACTIVE" or "VERIFIED")) return Problem(http, new("N11_CONNECTION_INACTIVE", "Kargo şablonları yalnız aktif veya doğrulanmış N11 bağlantısından okunur.", 409));
            var result = await n11.ShipmentTemplatesAsync(tenant.TenantId, id, http.RequestAborted);
            return result.IsSuccess ? Results.Ok(result.Value) : Problem(http, MarketplaceErrorStatus.ToServiceError(result.Error));
        }).LiveMarketplaceRead().Produces<IReadOnlyList<string>>();
        api.MapPut("/connections/{id:guid}/active", async (Guid id, ActiveCommand command, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.SetActiveAsync(tenant.TenantId, id, version, command.Active, http.RequestAborted), x => x.Version) : failure! : MissingContext(http)).WithServerBudget().RequireIdempotencyKey().Produces<ConnectionView>().IfMatch();
        api.MapPost("/connections/{id:guid}/initial-data-sync-jobs", async (Guid id, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            if (!TryIfMatch(http, out var version, out var failure)) return failure!;
            return Accepted(await service.EnqueueInitialDataSyncAsync(tenant.TenantId, id, http.Request.Headers["Idempotency-Key"].ToString(), version, http.TraceIdentifier, http.RequestAborted));
        }).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted).IfMatch();
        api.MapPost("/connections/{id:guid}/deep-delete", DeepDeleteConnectionAsync).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<OperationalDataResetView>().IfMatch();
        api.MapGet("/connections/{id:guid}/full-reset", async (Guid id, HttpContext http, IOperationalDataMaintenanceService service) => Tenant(http) is { } tenant ? Result(await service.FullResetAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http)).RequireGate(ApiGate.Elevated).Produces<ConnectionFullResetView>();
        api.MapPost("/connections/{id:guid}/full-reset", FullResetConnectionAsync).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<ConnectionFullResetView>().IfMatch();
        // Paneli Tamamen Sıfırla (owner and administrators only, RoleAuthorizationMiddleware): the session that asks stays signed in.
        api.MapGet("/settings/factory-reset", async (HttpContext http, IPanelFactoryResetService service) => Tenant(http) is { } tenant ? Result(await service.GetAsync(tenant.TenantId, http.RequestAborted), Results.Ok) : Unauthorized(http)).RequireGate(ApiGate.Elevated).Produces<PanelFactoryResetView>();
        api.MapPost("/settings/factory-reset", FactoryResetAsync).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<PanelFactoryResetView>();
        api.MapGet("/connections/{id:guid}/capabilities", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? Result(await service.CapabilitiesAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyList<CapabilityView>>();
        api.MapPost("/connections/{id:guid}/sync-policies/{resourceType}/runs", async (Guid id, string resourceType, HttpContext http, IScheduledJobProducer producer) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null
                ? Accepted(await producer.RunNowAsync(tenant.TenantId, id, resourceType, OperationCorrelation(http), http.RequestAborted))
                : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        // G: the automatic price/stock pipeline of every connection, and the kill switch that turns it off everywhere.
        api.MapGet("/price-stock-monitor", async (HttpContext http, IPriceStockMonitorService service) => Tenant(http) is { } tenant ? Results.Ok(await service.MonitorAsync(tenant.TenantId, http.RequestAborted)) : Unauthorized(http)).Produces<PriceStockMonitorView>();
        // API Sağlığı: per connection, the key / schedule lag / error rate / quota / queue age / webhook signals from stored data.
        api.MapGet("/api-health-signals", async (HttpContext http, IApiHealthSignalsService service) => Tenant(http) is { } tenant ? Results.Ok(await service.SignalsAsync(tenant.TenantId, http.RequestAborted)) : Unauthorized(http)).Produces<ApiHealthSignalsView>();
        // The lists behind the monitor's and the health cells' numbers for one connection (İşlem Takibi detail windows), read-only.
        api.MapGet("/price-stock-monitor/{connectionId:guid}/invalid-offers", async (Guid connectionId, HttpContext http, IPriceStockMonitorService service, string? search, string? reason, int? page, int? pageSize) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var request = NumberedPageRequest.From(page, pageSize);
            return Result(await service.InvalidOffersAsync(tenant.TenantId, connectionId, search, reason, request.Page, request.PageSize, http.RequestAborted), Results.Ok);
        }).Produces<PriceStockInvalidOffersView>();
        api.MapGet("/price-stock-monitor/{connectionId:guid}/failed-pushes", async (Guid connectionId, HttpContext http, IPriceStockMonitorService service, string? kind, int? page, int? pageSize) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var request = NumberedPageRequest.From(page, pageSize);
            return Result(await service.FailedPushesAsync(tenant.TenantId, connectionId, kind, request.Page, request.PageSize, http.RequestAborted), Results.Ok);
        }).Produces<PriceStockFailedPushesView>();
        api.MapGet("/price-stock-monitor/{connectionId:guid}/lost-links", async (Guid connectionId, HttpContext http, IPriceStockMonitorService service, string? health, int? page, int? pageSize) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var request = NumberedPageRequest.From(page, pageSize);
            return Result(await service.LostLinksAsync(tenant.TenantId, connectionId, health, request.Page, request.PageSize, http.RequestAborted), Results.Ok);
        }).Produces<PriceStockLostLinksView>();
        api.MapGet("/api-health-signals/{connectionId:guid}/attempts", async (Guid connectionId, HttpContext http, IApiHealthSignalsService service, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "class")] string? attemptClass, int? page, int? pageSize) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var request = NumberedPageRequest.From(page, pageSize);
            return Result(await service.AttemptsAsync(tenant.TenantId, connectionId, attemptClass, request.Page, request.PageSize, http.RequestAborted), Results.Ok);
        }).Produces<ApiHealthAttemptsView>();
        api.MapPost("/price-stock-monitor/emergency-stop", async (HttpContext http, IPriceStockMonitorService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            return Results.Ok(await service.StopAllAutomaticWritesAsync(tenant.TenantId, tenant.UserId, OperationCorrelation(http), http.RequestAborted));
        }).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<PriceStockEmergencyStopView>();
        api.MapGet("/connections/{id:guid}/sync-policies", async (Guid id, HttpContext http, IMarketplaceConnectionService service) => Tenant(http) is { } tenant ? Result(await service.SyncPoliciesAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<IReadOnlyList<SyncPolicyView>>();
        api.MapPut("/connections/{id:guid}/sync-policies/{resourceType}", async (Guid id, string resourceType, UpdateSyncPolicyCommand command, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http); var expected = OptionalIfMatch(http, out var malformed); if (malformed is not null) return malformed;
            var result = await service.UpsertSyncPolicyAsync(tenant.TenantId, id, resourceType, expected, command, http.RequestAborted, RoleAuthorizationMiddleware.IsElevated(tenant.Role), tenant.UserId);
            if (result.Error is { Code: "PERMISSION_DENIED" } denied) return Problem(http, denied);
            return Result(result, value => { http.Response.Headers.ETag = $"\"v{value.Version}\""; return Results.Ok(value); });
        }).Produces<SyncPolicyView>().IfMatch(required: false).Idempotent(WriteReasons.Replaces);
        // The automatic price/stock pair switched as one: both policies' versions are checked and both saved in one
        // transaction (two parallel PUTs left a pair half open on 18 Nov). The versions travel in the body, so a repeat is
        // refused as stale (412). The single-policy PUT above stays for older panels.
        api.MapPut("/connections/{id:guid}/external-write-group", async (Guid id, UpdateExternalWriteGroupCommand command, HttpContext http, IMarketplaceConnectionService service) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            var result = await service.UpdateExternalWriteGroupAsync(tenant.TenantId, id, command, http.RequestAborted, RoleAuthorizationMiddleware.IsElevated(tenant.Role), tenant.UserId);
            if (result.Error is { Code: "PERMISSION_DENIED" } denied) return Problem(http, denied);
            return Result(result, Results.Ok);
        }).RequireGate(ApiGate.Elevated).Produces<IReadOnlyList<SyncPolicyView>>().Idempotent(WriteReasons.VersionGuarded);
        // Owner rule of 18 Nov 2026: a linked variant without an offer goes at the panel price. The read-only counts the
        // switch's confirmation shows, and the owner's opening of the missing offers (marked as sent: nothing goes out).
        api.MapGet("/connections/{id:guid}/panel-price-offers", async (Guid id, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant ? Result(await service.PanelPriceOfferPreviewAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<PanelPriceOfferPreview>();
        api.MapPost("/connections/{id:guid}/panel-price-offers", async (Guid id, HttpContext http, IInventoryService service) =>
            Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.OpenPanelPriceOffersAsync(tenant.TenantId, tenant.UserId, id, http.RequestAborted), Results.Ok) : MissingContext(http))
            .RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<PanelPriceOffersOpened>();

        api.MapGet("/orders", async (HttpContext http, IMarketplaceSalesService service, int? limit, int? page, string? after, string? status, string? search, string? platform, string? platforms, string? listing, string? cargo, string? invoice, string? invoiceType, string? invoiceRegion, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? sort, bool? labelPrinted, DateTimeOffset? cancelFrom, DateTimeOffset? cancelTo, bool? partialCancel, bool? micro, bool? resentNotShipped) =>
            Tenant(http) is { } tenant
                ? Results.Ok(InvoiceLinkVisibility.ForRole(await service.OrdersAsync(tenant.TenantId, PageSize(limit), PageNumber(page), after, OrderQuery(status, search, platform, platforms, listing, cargo, invoice, invoiceType, invoiceRegion, dateFrom, dateTo, sort, labelPrinted, cancelFrom, cancelTo, partialCancel, micro, resentNotShipped), http.RequestAborted), tenant.Role))
                : Unauthorized(http)).Produces<PageResult<OrderListView>>();
        // Records that a shipping label of the order was printed, so every computer sees it.
        api.MapPost("/orders/{id:guid}/label-printed", async (Guid id, HttpContext http, IMarketplaceSalesService service) =>
            Tenant(http) is { } tenant
                ? await service.MarkLabelPrintedAsync(tenant.TenantId, id, http.RequestAborted) ? Results.NoContent() : Results.NotFound()
                : Unauthorized(http)).RequireGate(ApiGate.Working).Produces(StatusCodes.Status204NoContent).Idempotent("marks the order's label printed; a repeat marks it again");
        // Tab counters for the same filters as GET /orders (the status tab itself is ignored).
        api.MapGet("/orders/summary", async (HttpContext http, IMarketplaceSalesService service, string? search, string? platform, string? platforms, string? listing, string? cargo, string? invoice, string? invoiceType, string? invoiceRegion, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, bool? labelPrinted, string? tabs, DateTimeOffset? cancelFrom, DateTimeOffset? cancelTo, bool? partialCancel, bool? micro, bool? resentNotShipped) =>
            Tenant(http) is { } tenant
                // Every caller shares an identical answer while no order, package, claim or store of the tenant changed.
                ? Results.Ok(await service.OrderSummaryAsync(tenant.TenantId, OrderQuery(null, search, platform, platforms, listing, cargo, invoice, invoiceType, invoiceRegion, dateFrom, dateTo, null, labelPrinted, cancelFrom, cancelTo, partialCancel, micro, resentNotShipped), http.RequestAborted,
                    string.IsNullOrWhiteSpace(tabs) ? null : tabs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    allowCached: true))
                : Unauthorized(http)).Produces<OrderSummaryView>();
        // Orders per platform and cargo choice for the same filters as GET /orders, status tab included.
        api.MapGet("/orders/facets", async (HttpContext http, IMarketplaceSalesService service, string? status, string? search, string? platform, string? platforms, string? listing, string? cargo, string? invoice, string? invoiceType, string? invoiceRegion, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, bool? labelPrinted, DateTimeOffset? cancelFrom, DateTimeOffset? cancelTo, bool? partialCancel, bool? micro, bool? resentNotShipped) =>
            Tenant(http) is { } tenant
                ? Results.Ok(await service.OrderFacetsAsync(tenant.TenantId, OrderQuery(status, search, platform, platforms, listing, cargo, invoice, invoiceType, invoiceRegion, dateFrom, dateTo, null, labelPrinted, cancelFrom, cancelTo, partialCancel, micro, resentNotShipped), http.RequestAborted))
                : Unauthorized(http)).Produces<OrderFacetsView>();
        api.MapPost("/orders/shopify-csv-import", ImportShopifyOrdersAsync).DisableAntiforgery().RequireIdempotencyKey().Produces<ShopifyOrderCsvImportResult>();
        api.MapGet("/orders/product-image", async (HttpContext http, IMarketplaceSalesService service, string? barcode, Guid? connectionId, string? productName, string? size) => Tenant(http) is { } tenant ? Result(await service.ProductImageAsync(tenant.TenantId, barcode, http.TraceIdentifier, http.RequestAborted, connectionId, productName), value => Results.Redirect(size == "thumb" ? ProductImageThumbnailUrl(value) : value)) : Unauthorized(http)).LiveMarketplaceRead().Produces(StatusCodes.Status302Found);
        api.MapGet("/orders/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? WithEtag(http, InvoiceLinkVisibility.ForRole(await service.OrderAsync(tenant.TenantId, id, http.RequestAborted), tenant.Role), x => x.Version) : Unauthorized(http)).Produces<OrderDetailView>();
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
        }).LiveMarketplaceRead().Produces<OrderFinanceView>();
        api.MapPut("/orders/{id:guid}/shopify-status", UpdateShopifyOrderStatusAsync).RequireIdempotencyKey().Produces<ShopifyOrderStatusResult>().IfMatch();
        api.MapPost("/orders/{id:guid}/instant-process", async (Guid id, HttpContext http, AppDbContext db, IMarketplaceSalesService service, IMarketplaceJobProcessor marketplaceProcessor) =>
        {
            if (Tenant(http) is not { } tenant) return Unauthorized(http);
            if (RequireIdempotency(http) is { } missingContext) return missingContext;
            return await InstantProcessOrderAsync(id, tenant, http, db, service, marketplaceProcessor, http.RequestAborted);
        }).WithServerBudget().RequireIdempotencyKey().Produces<ShipmentView>();
        api.MapPost("/connections/{connectionId:guid}/order-sync-jobs", async (Guid connectionId, OrderSyncCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueOrderSyncAsync(tenant.TenantId, connectionId, command.ExternalOrderId, command.Full, http.TraceIdentifier, http.RequestAborted, command.PackageNumber)) : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/connections/{connectionId:guid}/product-sync-jobs", async (Guid connectionId, bool? full, bool? newOnly, bool? existingOnly, bool? mappingOnly, bool? optionsOnly, bool? includeArchived, bool? includeDrafts, bool? includePendingApproval, bool? updateExistingProducts, bool? updatePrices, bool? updateStock, bool? updateExistingPricesAndStock, string? lookup, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueProductSyncAsync(tenant.TenantId, connectionId, full == true, newOnly == true, existingOnly == true, mappingOnly == true, optionsOnly == true, includeArchived == true, includeDrafts == true, includePendingApproval == true, updateExistingProducts != false, (updatePrices ?? updateExistingPricesAndStock) == true, (updateStock ?? updateExistingPricesAndStock) == true, lookup, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/connections/{connectionId:guid}/stage-test-order-jobs", async (Guid connectionId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueStageTestOrderAsync(tenant.TenantId, tenant.UserId, connectionId, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : MissingContext(http)).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/connections/{connectionId:guid}/catalog-structure-runs", async (Guid connectionId, string? mode, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.EnqueueCatalogStructureAsync(tenant.TenantId, connectionId, mode ?? "PREVIEW", http.TraceIdentifier, http.RequestAborted), value => Results.Accepted($"/api/v1/catalog-structure-runs/{value.Id:D}", value)) : MissingContext(http)).RequireIdempotencyKey().Produces<CatalogStructureRunView>(StatusCodes.Status202Accepted);
        api.MapGet("/connections/{connectionId:guid}/catalog-structure-runs", async (Guid connectionId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Results.Ok(await service.CatalogStructureRunsAsync(tenant.TenantId, connectionId, http.RequestAborted)) : Unauthorized(http)).Produces<IReadOnlyList<CatalogStructureRunView>>();
        api.MapGet("/catalog-structure-runs/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.CatalogStructureRunAsync(tenant.TenantId, id, http.RequestAborted), Results.Ok) : Unauthorized(http)).Produces<CatalogStructureRunView>();
        api.MapPost("/connections/{connectionId:guid}/reference-sync-jobs", async (Guid connectionId, string? resourceType, string? parentExternalId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReferenceSyncAsync(tenant.TenantId, connectionId, resourceType ?? "CATEGORIES", parentExternalId, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/orders/{id:guid}/packages", async (Guid id, OrderPackageCreateRequest command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.CreateOrderPackageInstantAsync(tenant.TenantId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), Results.Ok) : failure! : MissingContext(http)).WithServerBudget().RequireIdempotencyKey().Produces<CreateOrderPackageResult>().IfMatch();
        api.MapGet("/shipments", async (HttpContext http, IMarketplaceSalesService service, int? limit, string? after, string? status) => Tenant(http) is { } tenant ? Results.Ok(await service.ShipmentsAsync(tenant.TenantId, PageSize(limit), after, status, http.RequestAborted)) : Unauthorized(http)).Produces<PageResult<ShipmentView>>();
        api.MapGet("/shipments/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.ShipmentAsync(tenant.TenantId, id, http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Package.Version}\""; return Results.Ok(value); }) : Unauthorized(http)).Produces<ShipmentDetailView>();
        api.MapGet("/shipments/{id:guid}/changeable-cargo-companies", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.ChangeableCargoCompaniesAsync(tenant.TenantId, id, OperationCorrelation(http), http.RequestAborted), Results.Ok) : Unauthorized(http)).LiveMarketplaceRead().Produces<IReadOnlyList<RemoteCargoCompany>>();
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
        }).RequireGate(ApiGate.Operations).ProducesFile("application/pdf", "application/zpl", "text/plain", "image/png", "image/jpeg");
        // INVOICED tells the marketplace the package's invoice number: like issuing and sending an invoice, it is for
        // the owner and administrators (the role gate leaves the other package actions to operations).
        api.MapPost("/shipments/{id:guid}/actions", async (Guid id, ShipmentActionCommand command, HttpContext http, IMarketplaceSalesService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            if (string.Equals(command.Action?.Trim(), "INVOICED", StringComparison.OrdinalIgnoreCase) && !RoleAuthorizationMiddleware.IsElevated(tenant.Role))
                return Problem(http, new("PERMISSION_DENIED", "Pazaryerine fatura bilgisi göndermek yalnız sahip ve yöneticiler içindir.", 403));
            return TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueShipmentActionAsync(tenant.TenantId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted)) : failure!;
        }).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted).IfMatch();
        api.MapPost("/shipments/{id:guid}/instant-process", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.ProcessShipmentInstantAsync(tenant.TenantId, id, version, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Version}\""; return Results.Ok(value); }) : failure! : MissingContext(http)).WithServerBudget().RequireIdempotencyKey().Produces<ShipmentView>().IfMatch();
        api.MapPost("/shipments/{id:guid}/instant-cargo-provider", async (Guid id, ShipmentActionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Result(await service.ChangeCargoProviderInstantAsync(tenant.TenantId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), value => { http.Response.Headers.ETag = $"\"v{value.Version}\""; return Results.Ok(value); }) : failure! : MissingContext(http)).WithServerBudget().RequireIdempotencyKey().Produces<ShipmentView>().IfMatch();
        api.MapPost("/shipments/{id:guid}/common-label-jobs", async (Guid id, CommonLabelCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueCommonLabelAsync(tenant.TenantId, id, version, command.BoxQuantity, command.VolumetricHeight, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted, command.Format)) : failure! : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted).IfMatch();
        api.MapPost("/shipments/{id:guid}/label-capability-probes", async (Guid id, LabelCapabilityProbeCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? Accepted(await service.EnqueueLabelCapabilityProbeAsync(tenant.TenantId, tenant.UserId, id, version, command.CapabilityCode, command.BoxQuantity, command.VolumetricHeight, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted)) : failure! : MissingContext(http)).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted).IfMatch();
        api.MapGet("/returns", async (HttpContext http, IMarketplaceSalesService service, int? limit, int? page, string? after, string? status, string? customer, string? orderNumber, string? claimCode, string? barcode, string? reason, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, bool? latest, string? search, string? platforms, string? cargo, string? reasonKey, string? invoiceStatus, string? sort) => Tenant(http) is { } tenant ? Results.Ok(await service.ReturnsAsync(tenant.TenantId, PageSize(limit), after, ReturnQuery(status, customer, orderNumber, claimCode, barcode, reason, dateFrom, dateTo, search, platforms, cargo, reasonKey, invoiceStatus, sort), latest == true, http.RequestAborted, PageNumber(page))) : Unauthorized(http)).Produces<PageResult<ReturnListView>>();
        // Tab counts and filter choices of the Returns page, for the same filters as GET /returns.
        api.MapGet("/returns/summary", async (HttpContext http, IMarketplaceSalesService service, string? status, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? search, string? platforms, string? cargo, string? reasonKey, string? invoiceStatus) => Tenant(http) is { } tenant ? Results.Ok(await service.ReturnSummaryAsync(tenant.TenantId, ReturnQuery(status, null, null, null, null, null, dateFrom, dateTo, search, platforms, cargo, reasonKey, invoiceStatus, null), http.RequestAborted)) : Unauthorized(http)).Produces<ReturnSummaryView>();
        // Reads the return's status history from Trendyol live (getClaimItemAudits); nothing is stored or written.
        api.MapGet("/returns/{id:guid}/audits", async (Guid id, HttpContext http, IReturnAuditReader reader) => Tenant(http) is { } tenant ? Result(await reader.ReturnAuditsAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted), Results.Ok) : Unauthorized(http)).LiveMarketplaceRead().Produces<IReadOnlyList<ReturnLineAuditView>>();
        api.MapGet("/returns/{id:guid}", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? WithEtag(http, await service.ReturnAsync(tenant.TenantId, id, http.RequestAborted), x => x.Version) : Unauthorized(http)).Produces<ReturnDetailView>();
        api.MapGet("/returns/{id:guid}/return-shipment-companies", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.ReturnShipmentCompaniesAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted), Results.Ok) : Unauthorized(http)).LiveMarketplaceRead().Produces<IReadOnlyList<ReturnShipmentCompany>>();
        api.MapGet("/returns/{id:guid}/rejection-reasons", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant ? Result(await service.ReturnIssueReasonsAsync(tenant.TenantId, id, http.TraceIdentifier, http.RequestAborted), Results.Ok) : Unauthorized(http)).LiveMarketplaceRead().Produces<IReadOnlyList<ReturnIssueReason>>();
        api.MapPost("/connections/{connectionId:guid}/return-sync-jobs", async (Guid connectionId, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Accepted(await service.EnqueueReturnSyncAsync(tenant.TenantId, connectionId, http.TraceIdentifier, http.RequestAborted)) : MissingContext(http)).RequireIdempotencyKey().Produces<JobAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/returns/{id:guid}/received", async (Guid id, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.MarkReturnReceivedAsync(tenant.TenantId, tenant.UserId, id, version, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), x => x.Version) : failure! : MissingContext(http)).WithServerBudget().RequireIdempotencyKey().Produces<ReturnDetailView>().IfMatch();
        api.MapPost("/returns/{id:guid}/actions", async (Guid id, ReturnDecisionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? TryIfMatch(http, out var version, out var failure) ? WithEtag(http, await service.ProcessReturnActionInstantAsync(tenant.TenantId, tenant.UserId, id, version, command, http.Request.Headers["Idempotency-Key"].ToString(), OperationCorrelation(http), http.RequestAborted), x => x.Version) : failure! : MissingContext(http)).WithServerBudget().RequireIdempotencyKey().Produces<ReturnDetailView>().IfMatch();
        api.MapPost("/returns/{id:guid}/stock-dispositions", async (Guid id, ReturnDispositionCommand command, HttpContext http, IMarketplaceSalesService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? WithEtag(http, await service.ApplyDispositionAsync(tenant.TenantId, tenant.UserId, id, command, http.Request.Headers["Idempotency-Key"].ToString(), http.TraceIdentifier, http.RequestAborted), x => x.Version) : MissingContext(http)).RequireIdempotencyKey().Produces<ReturnDetailView>();
        // Owner, 18 Nov 2026: "Değişim Onaylandı Olarak İşaretle" / "İşareti Kaldır" for a Trendyol exchange request Trendyol
        // answered Rejected. Panel only, nothing goes to Trendyol; owners and admins only.
        api.MapPost("/returns/{id:guid}/exchange-approved", async (Guid id, ReturnExchangeMarkCommand command, HttpContext http, IMarketplaceSalesService service) =>
        {
            if (Tenant(http) is not { } tenant || RequireIdempotency(http) is not null) return MissingContext(http);
            if (!RoleAuthorizationMiddleware.IsElevated(tenant.Role))
                return Problem(http, new("PERMISSION_DENIED", "Değişim onayı işareti yalnız sahip ve yöneticiler içindir.", 403));
            return TryIfMatch(http, out var version, out var failure)
                ? WithEtag(http, await service.MarkExchangeApprovedAsync(tenant.TenantId, tenant.UserId, id, version, command.Marked, OperationCorrelation(http), http.RequestAborted), x => x.Version)
                : failure!;
        }).RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<ReturnDetailView>().IfMatch();
        api.MapPost("/files/return-evidence", UploadReturnEvidenceAsync).DisableAntiforgery().RequireIdempotencyKey().Produces<ReturnEvidenceView>(StatusCodes.Status201Created);

        api.MapGet("/connections/{id:guid}/instant-notifications", async (Guid id, HttpContext http, IInstantNotificationService service) => Tenant(http) is { } tenant ? Result(await service.StatusAsync(tenant.TenantId, id, OperationCorrelation(http), http.RequestAborted), Results.Ok) : Unauthorized(http)).LiveMarketplaceRead().Produces<InstantNotificationView>();
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
        }).WithServerBudget().RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<InstantNotificationView>();
        api.MapDelete("/connections/{id:guid}/instant-notifications", async (Guid id, HttpContext http, IInstantNotificationService service) => Tenant(http) is { } tenant && RequireIdempotency(http) is null ? Result(await service.DisableAsync(tenant.TenantId, id, OperationCorrelation(http), http.RequestAborted), Results.Ok) : MissingContext(http)).WithServerBudget().RequireGate(ApiGate.Elevated).RequireIdempotencyKey().Produces<InstantNotificationView>();
        // Hepsiburada sends every event kind to its own path below the base address, some with PUT.
        api.MapMethods("/hooks/{connectionPublicId:guid}/{routeToken}", ["POST", "PUT"], ReceiveWebhook).DisableAntiforgery().RequireGate(ApiGate.Open).Produces<WebhookAcceptedView>().Idempotent("a repeated delivery of the same marketplace message is queued once (deduplicated by its message id)");
        // An event path answers as Hepsiburada's contracts show: a POST 201, a PUT 204, both without a body.
        api.MapMethods("/hooks/{connectionPublicId:guid}/{routeToken}/{**eventPath}", ["POST", "PUT"], ReceiveWebhookEvent).DisableAntiforgery().RequireGate(ApiGate.Open)
            .Produces(StatusCodes.Status201Created).Produces(StatusCodes.Status204NoContent).Idempotent("a repeated delivery of the same marketplace message is queued once (deduplicated by its message id)");
        return endpoints;
    }

    internal static Task<IResult> ReceiveWebhookEvent(Guid connectionPublicId, string routeToken, string? eventPath, HttpContext http, IMarketplaceWebhookService service, IWebhookRouteGate gate, WebhookConnectionQuota quota) =>
        ReceiveWebhookCore(connectionPublicId, routeToken, http, service, gate, quota, eventPath);

    internal static async Task<IResult> ReceiveWebhook(Guid connectionPublicId, string routeToken, HttpContext http, IMarketplaceWebhookService service, IWebhookRouteGate gate, WebhookConnectionQuota quota) =>
        await ReceiveWebhookCore(connectionPublicId, routeToken, http, service, gate, quota, null);

    private static async Task<IResult> ReceiveWebhookCore(Guid connectionPublicId, string routeToken, HttpContext http, IMarketplaceWebhookService service, IWebhookRouteGate gate, WebhookConnectionQuota quota, string? eventPath)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // The address (connection and route token) is checked before anything is read: an unknown one costs no body buffering.
        if (!await gate.IsKnownAsync(connectionPublicId, routeToken, http.RequestAborted))
        {
            // A known connection called with an old or wrong address is shown on its webhook panel; the body stays unread.
            await service.NoteRouteRefusedAsync(connectionPublicId, http.RequestAborted);
            return Problem(http, new("WEBHOOK_ROUTE_NOT_FOUND", "Webhook route bulunamadı.", 404));
        }
        // The connection's allowance is spent by admitted deliveries only (WebhookRateLimits).
        if (!quota.TryTake(connectionPublicId)) return Problem(http, MarketplaceHub.Api.Security.RateLimitPolicies.Rejected);
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
                .LogWarning("Webhook acknowledged but not processed (stale delivery or paused connection) for connection {ConnectionPublicId}.", connectionPublicId);
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
    private static ReturnListQuery ReturnQuery(string? status, string? customer, string? orderNumber, string? claimCode, string? barcode, string? reason, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? search, string? platforms, string? cargo, string? reasonKey, string? invoiceStatus, string? sort) =>
        new(status, customer, orderNumber, claimCode, barcode, reason, dateFrom, dateTo, search,
            platforms?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), cargo, reasonKey, invoiceStatus, sort);
    private static OrderListQuery OrderQuery(string? status, string? search, string? platform, string? platforms, string? listing, string? cargo, string? invoice, string? invoiceType, string? invoiceRegion, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, string? sort, bool? labelPrinted, DateTimeOffset? cancelFrom = null, DateTimeOffset? cancelTo = null, bool? partialCancel = null, bool? microExport = null, bool? resentNotShipped = null) =>
        new(status, search, platform, listing, cargo, invoice, invoiceType, invoiceRegion, dateFrom, dateTo, sort, platforms?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), labelPrinted, cancelFrom, cancelTo, partialCancel, microExport, resentNotShipped);
    private static IResult WithEtag<T>(HttpContext http, ServiceResult<T> result, Func<T, long> version) => EndpointHelpers.WithEtag(http, result, version, MarketplaceErrorStatus.ForPanel);
    private static IResult Created<T>(ServiceResult<T> result, string collection) => EndpointHelpers.Created(result, collection, MarketplaceErrorStatus.ForPanel);
    private static IResult Accepted(ServiceResult<Guid> result) => EndpointHelpers.Accepted(result, MarketplaceErrorStatus.ForPanel);
    private static IResult Result<T>(ServiceResult<T> result, Func<T, IResult> success) => EndpointHelpers.Result(result, success, MarketplaceErrorStatus.ForPanel);
    // ?size=thumb: a small frame asks Trendyol's CDN for its small copy (/mnresize/256/384/, ~10 KB) instead of the
    // ~145 KB full photo, the same rule as the panel's thumbnailUrl (shared/product-thumbnail.ts). Other hosts stay as
    // they are (Hepsiburada's size token has no documented sizes).
    internal static string ProductImageThumbnailUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, "cdn.dsmcdn.com", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/mnresize/", StringComparison.Ordinal))
            return url;
        var path = System.Text.RegularExpressions.Regex.Replace(uri.AbsolutePath, "/{2,}", "/");
        return new UriBuilder(uri) { Path = "/mnresize/256/384" + path }.Uri.AbsoluteUri;
    }
    public sealed record ActiveCommand(bool Active);
    public sealed record InstantNotificationCommand(string? ClientSecret, bool RestorePrevious = false, bool AdoptFromInactive = false);
    public sealed record OrderSyncCommand(string? ExternalOrderId, bool Full = false, string? PackageNumber = null);
    public sealed record CommonLabelCommand(int BoxQuantity, decimal VolumetricHeight, string Format = "ZPL");
    public sealed record LabelCapabilityProbeCommand(string CapabilityCode, int BoxQuantity, decimal VolumetricHeight);
}
