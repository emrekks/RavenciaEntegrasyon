using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed partial class TrendyolHttpClient
{
    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var authorized = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken); if (authorized is null) return AdapterResult<ConnectionIdentity>.Failure(TrendyolErrorMapper.Configuration());
        var result = await SendAsync(authorized, HttpMethod.Get, TrendyolEndpoints.OrderStream(authorized.Connection.ExternalStoreId) + "?size=1", null, cancellationToken); if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        return AdapterResult<ConnectionIdentity>.Success(new("TRENDYOL", authorized.Connection.Environment, authorized.Connection.ExternalStoreId, "V2", authorized.Connection.ExternalStoreId), result.RateLimit);
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var test = await TestAsync(context, cancellationToken); if (!test.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(test.Error!, test.RateLimit);
        var identity = test.Value!; var now = timeProvider.GetUtcNow();
        var references = await ReadAsync(context, new("CATEGORIES", null), new(null, 1), cancellationToken);
        var products = await ListAsync(context, new(null, 1), new(null), cancellationToken);
        var returns = await PollAsync(context, new ReturnPollWindow(null, null), new(null, 1), cancellationToken);
        IReadOnlyList<CapabilityEvidence> evidence =
        [
            SupportedEvidence(MarketplaceCapabilities.ConnectionTest, identity, "https://developers.trendyol.com/v2.0/docs/authorization", "Stage/Production kimlik doğrulaması order stream read ile geçti.", now),
            SupportedEvidence(MarketplaceCapabilities.OrderRead, identity, "https://developers.trendyol.com/v2.0/docs/getshipmentpackagesstream", "Cursor order stream read yanıtı alındı.", now),
            ReadProbeEvidence(MarketplaceCapabilities.ReferenceRead, identity, "https://developers.trendyol.com/v2.0/docs/trendyol-category-list-getcategorytree", references, "Kategori ağacı", now),
            ReadProbeEvidence(MarketplaceCapabilities.ProductRead, identity, "https://developers.trendyol.com/v2.0/docs/product-filtering-approved-products-v2", products, "Onaylı ürün listesi", now),
            ReadProbeEvidence(MarketplaceCapabilities.ReturnRead, identity, "https://developers.trendyol.com/v2.0/docs/getting-returned-orders-getclaims", returns, "İade talepleri", now),
            SupportedEvidence(MarketplaceCapabilities.QuestionRead, identity, "https://developers.trendyol.com/docs/m%C3%BC%C5%9Fteri-sorular%C4%B1n%C4%B1-%C3%A7ekme", "Ürün soruları ve yazışma geçmişi için Trendyol'un resmî müşteri soru listeleme endpoint'i doğrulandı.", now),
            SupportedEvidence(MarketplaceCapabilities.QuestionWrite, identity, "https://developers.trendyol.com/docs/m%C3%BC%C5%9Fteri-sorular%C4%B1n%C4%B1-cevaplama", "Cevap gönderme yeteneği resmî endpoint dokümanına göre kaydedildi; bağlantı kontrolünde dış yazma çağrısı çalıştırılmaz.", now)
        ];
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence, test.RateLimit);
    }
    private static CapabilityEvidence SupportedEvidence(string code, ConnectionIdentity identity, string sourceUrl, string note, DateTimeOffset verifiedAt) =>
        new(code, "SUPPORTED", "V2", identity.Environment, identity.ExternalStoreId, sourceUrl, "2026-08-04", null, null, note, null, verifiedAt);

    private static CapabilityEvidence ReadProbeEvidence<T>(string code, ConnectionIdentity identity, string sourceUrl, AdapterResult<T> probe, string label, DateTimeOffset verifiedAt) =>
        probe.IsSuccess
            ? SupportedEvidence(code, identity, sourceUrl, $"{label} salt-okunur probu başarılı yanıt aldı.", verifiedAt)
            : new(code, "UNKNOWN", "V2", identity.Environment, identity.ExternalStoreId, sourceUrl, "2026-08-04", null, null, $"{label} salt-okunur probu kanıt üretmedi: {probe.Error?.Code ?? "UNKNOWN_ERROR"}.", null, verifiedAt);
}
