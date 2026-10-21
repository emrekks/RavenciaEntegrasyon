using System.Globalization;
using System.Net;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Product reads (phase 3). StokKontrolListesi pages the store's products (SearchPage from 0, every status with
// SearchAktifPasif=0) and names the total in RowCount; it has no modification filter, so a scan walks every page.
// A single product is found by its barcode (SearchBarkod). The page cursor is "{page}:{firstBarcode}": the first
// barcode of page zero guards against a server that ignores SearchPage and keeps answering the first page.
// Product send (create or update, single-variant products) is in PttAvmProductWrites.cs; the other write calls stay closed.
public sealed partial class PttAvmHttpClient : IProductPort
{
    // An answer that never runs dry still ends: PTT AVM's documented page cap.
    internal const int MaxProductPages = 1000;
    private const string ProductWritesLater = "PTT AVM ürün güncellemesi ürün gönderimiyle (yeniden yayınla) yapılır; bu işlem PTT AVM için ayrıca desteklenmiyor.";

    public async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<AdapterPageResult<RemoteCatalogProduct>>(load);
        return await ReadProductPageAsync(load.Account, page.Cursor, LookupBarcode(filter), cancellationToken);
    }

    public async Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
    {
        var catalog = await ListCatalogAsync(context, page, filter, cancellationToken);
        if (!catalog.IsSuccess) return AdapterResult<AdapterPageResult<RemoteProduct>>.Failure(catalog.Error!, catalog.RateLimit);
        var items = catalog.Value!.Items
            .SelectMany(product => product.Variants.Select(variant => new RemoteProduct(product.ExternalProductId, variant.ExternalVariantId, variant.Barcode, variant.Sku, variant.RawJson == "{}" ? product.RawJson : variant.RawJson)))
            .ToList();
        return AdapterResult<AdapterPageResult<RemoteProduct>>.Success(new(items, catalog.Value.NextCursor, catalog.Value.HasMore, catalog.Value.TotalCount, catalog.Value.Issues), catalog.RateLimit);
    }

    internal async Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ReadProductPageAsync(PttAvmRequestContext account, string? cursor, string? barcode, CancellationToken cancellationToken)
    {
        var (pageIndex, firstBarcode) = ProductCursor(cursor);
        var result = await SendSoapAsync(account, StockList, StockListParameters(account.Connection.ExternalStoreId, pageIndex, barcode), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(result.Error!, result.RateLimit);
        if (!IsNil(result.Value!) && result.Value!.Elements().Any(x => x.Name.LocalName != "StokKontrolDetay"))
            return Failure<AdapterPageResult<RemoteCatalogProduct>>(AdapterErrorClass.ContractViolation, "PTTAVM_PRODUCT_CONTRACT_INVALID", $"PTT AVM ürün listesi beklenen yapıda değil (yanıt öğeleri: {ElementShape(result.Value)}).", HttpStatusCode.BadGateway);
        // The variants of model products come from GetProductsWithVariants: read on the first page of a scan, reused by
        // its later pages. A failure there leaves the products without their variants and is reported, not fatal.
        var variantList = await VariantListAsync(account, refresh: pageIndex == 0 && barcode is null, cancellationToken);
        var (products, total, issues) = PttAvmProductMapper.Page(result.Value!, variantList.IsSuccess ? variantList.Value : null);
        var pageFirst = products.FirstOrDefault()?.ProductMainId;
        var issueList = issues.ToList();
        if (!variantList.IsSuccess)
            issueList.Add(new("PTTAVM_VARIANT_LIST_UNAVAILABLE", pageIndex.ToString(CultureInfo.InvariantCulture), $"PTT AVM varyant listesi okunamadı ({variantList.Error!.Code}); ürünler bu sayfada varyantsız okundu."));
        // A later page that starts like page zero means SearchPage was ignored: the scan stops instead of looping.
        if (pageIndex > 0 && pageFirst is not null && string.Equals(pageFirst, firstBarcode, StringComparison.Ordinal))
        {
            issueList.Add(new("PTTAVM_PRODUCT_PAGE_REPEATED", pageIndex.ToString(CultureInfo.InvariantCulture), "PTT AVM ürün listesi sonraki sayfa yerine ilk sayfayı yeniden döndürdü; tarama bu sayfada durduruldu."));
            return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new([], null, false, total, issueList), result.RateLimit);
        }
        var anchor = pageIndex == 0 ? pageFirst : firstBarcode;
        var hasMore = barcode is null && products.Count > 0 && pageIndex + 1 < MaxProductPages && MorePages(pageIndex, products.Count, total);
        return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(
            new(products, hasMore ? ProductCursorText(pageIndex + 1, anchor) : null, hasMore, total, issueList), result.RateLimit);
    }

    // With RowCount the scan stops once the pages read cover it; without it, it reads on until an empty page.
    internal static bool MorePages(int pageIndex, int pageItems, int? total) =>
        total is not { } rows || (long)(pageIndex + 1) * pageItems < rows;

    internal static (int Page, string? FirstBarcode) ProductCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (0, null);
        var separator = cursor.IndexOf(':', StringComparison.Ordinal);
        var pageText = separator < 0 ? cursor : cursor[..separator];
        var page = int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : 0;
        var first = separator < 0 ? null : cursor[(separator + 1)..];
        return (page, string.IsNullOrEmpty(first) ? null : first);
    }

    internal static string ProductCursorText(int page, string? firstBarcode) => $"{page.ToString(CultureInfo.InvariantCulture)}:{firstBarcode}";

    // The import screen's lookup box and a barcode lookup both search by barcode.
    private static string? LookupBarcode(ProductReadFilter filter) =>
        !string.IsNullOrWhiteSpace(filter.Barcode) ? filter.Barcode.Trim() : !string.IsNullOrWhiteSpace(filter.ProductMainId) ? filter.ProductMainId.Trim() : null;

    // Every product of the store (active and passive, in stock or not), the filters of the documented sample.
    internal static IReadOnlyList<SoapParameter> StockListParameters(string storeId, int page, string? barcode = null) =>
    [
        new("ShopId", StoreId(storeId) ?? "0"), new("SearchKategoriId", "0"), new("SearchAltKategoriId", "0"), new("SearchUrunAdi", ""), new("SearchBarkod", barcode ?? ""),
        new("SearchAktifPasif", "0"), new("SearchMevcut", "0"), new("SearchTedarikciKategoriId", "0"), new("SearchYeniKategoriId", ""), new("SearchPage", page.ToString(CultureInfo.InvariantCulture))
    ];

    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<RemoteOperationRef>("PTTAVM_PRODUCT_WRITE_LATER", ProductWritesLater));

    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<RemoteOperationRef>("PTTAVM_PRODUCT_WRITE_LATER", ProductWritesLater));

    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<RemoteOperationRef>("PTTAVM_PRODUCT_WRITE_LATER", ProductWritesLater));

    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<RemoteOperationRef>("PTTAVM_PRODUCT_WRITE_LATER", ProductWritesLater));

    public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) =>
        Task.FromResult(NotSupported<RemoteOperationRef>("PTTAVM_PRODUCT_WRITE_LATER", ProductWritesLater));
}
