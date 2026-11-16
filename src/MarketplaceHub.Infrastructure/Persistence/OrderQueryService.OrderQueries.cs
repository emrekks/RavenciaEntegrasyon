using System.Linq.Expressions;
using System.Text;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class OrderQueryService
{
    // A micro-export order as the row's flag reads it (RowMarks): Trendyol's micro = true in the package snapshot.
    private Expression<Func<Order, bool>> MicroExportOrder() => x =>
        (x.CustomerSnapshotJson.Contains("\"micro\":true") || x.CustomerSnapshotJson.Contains("\"micro\": true"))
        && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == x.ConnectionId && connection.PlatformCode == "TRENDYOL");

    private Expression<Func<Order, bool>> DomesticOrder() => x =>
        !((x.CustomerSnapshotJson.Contains("\"micro\":true") || x.CustomerSnapshotJson.Contains("\"micro\": true"))
            && db.PlatformConnections.Any(connection => connection.TenantId == x.TenantId && connection.Id == x.ConnectionId && connection.PlatformCode == "TRENDYOL"));

    internal IQueryable<Order> CurrentManualShopifyOrderStatuses(Guid tenantId) => db.Orders.AsNoTracking().Where(HasCurrentManualShopifyStatus(tenantId));

    // A Shopify order of the tenant whose current status was set in the panel (O13). The tenant's Shopify stores are one
    // uncorrelated list, which PostgreSQL reads once per query (a hashed subplan); the store lookup per order it replaced
    // ran once for every order of every tab (live 12 Nov 2026: ~4,500 store-table scans per order summary count).
    private Expression<Func<Order, bool>> HasCurrentManualShopifyStatus(Guid tenantId)
    {
        var shopifyConnectionIds = db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && connection.PlatformCode == "SHOPIFY")
            .Select(connection => connection.Id);
        return order => order.TenantId == tenantId
            && shopifyConnectionIds.Contains(order.ConnectionId)
            && db.OrderStatusHistory.Any(history => history.TenantId == order.TenantId
                && history.OrderId == order.Id
                && history.RawStatus.StartsWith("MANUAL_SHOPIFY_STATUS:")
                && history.CanonicalStatus == order.DerivedStatus
                && history.OccurredAt >= order.LastRemoteModifiedAt);
    }

    // "Unpackaged" means no live package: a package the panel unpacked (UNPACKED_BY_PANEL, history
    // only) or a cancelled one leaves the order unpackaged again, so it is back in Yeni (O14).
    private IQueryable<Order> HepsiburadaUnpackagedNewOrders(Guid tenantId)
    {
        var verificationCutoff = OpenOrderLifecyclePolicy.HepsiburadaUnpackagedOrderVerificationCutoff(timeProvider.GetUtcNow());
        return db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && order.DerivedStatus == "NEW"
                && order.OrderedAt >= verificationCutoff
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                    && connection.Id == order.ConnectionId
                    && connection.PlatformCode == "HEPSIBURADA"
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                && !CurrentShipmentPackages().Any(package => package.TenantId == tenantId && package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled));
    }

    internal IQueryable<Order> HepsiburadaUnpackagedOnHoldOrders(Guid tenantId)
    {
        var verificationCutoff = OpenOrderLifecyclePolicy.HepsiburadaUnpackagedOrderVerificationCutoff(timeProvider.GetUtcNow());
        return db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && order.DerivedStatus == "ON_HOLD"
                && order.OrderedAt >= verificationCutoff
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                    && connection.Id == order.ConnectionId
                    && connection.PlatformCode == "HEPSIBURADA"
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                && !CurrentShipmentPackages().Any(package => package.TenantId == tenantId && package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled));
    }

    internal IQueryable<Order> HepsiburadaUnpackagedDeliveredOrders(Guid tenantId)
    {
        var verificationCutoff = OpenOrderLifecyclePolicy.HepsiburadaUnpackagedOrderVerificationCutoff(timeProvider.GetUtcNow());
        return db.Orders.AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && order.DerivedStatus == "DELIVERED"
                && order.OrderedAt >= verificationCutoff
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId
                    && connection.Id == order.ConnectionId
                    && connection.PlatformCode == "HEPSIBURADA"
                    && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))
                && !CurrentShipmentPackages().Any(package => package.TenantId == tenantId && package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled));
    }

    internal IQueryable<Order> ExcludeStaleUnpackagedHepsiburadaOrders(IQueryable<Order> query, Guid tenantId)
    {
        return query.ExcludeStaleUnpackaged(db, tenantId, timeProvider.GetUtcNow());
    }

    internal static bool SearchesCustomerSnapshot(string term) => term.Any(char.IsLetter);

    internal const string LikeEscape = "\\";

    // "%term%" for ILIKE as typed, and Turkish-folded (TurkishSearchText.Fold) for a column folded the
    // same way (TurkishSearchText.FoldedILike), with the LIKE wildcards of the term escaped (a "%" in
    // the term is a percent sign).
    internal static (string AsTyped, string Folded) SearchPatterns(string term)
    {
        static string Pattern(string value) => "%" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return (Pattern(term), Pattern(TurkishSearchText.Fold(term)));
    }

    // A PostgreSQL (and .NET) regular expression that finds the term in a customer snapshot value
    // whose key is not an e-mail field ("email", "customerEmail", ...): a list search matches names
    // and places, never e-mail addresses. It is matched case-insensitively (~*), and the term's
    // Turkish lower and upper forms are alternatives, so "ışık" finds "IŞIK" and "istanbul" "İSTANBUL".
    internal static string CustomerSnapshotSearchPattern(string term)
    {
        static string Escape(string value)
        {
            var escaped = new StringBuilder(value.Length * 2);
            foreach (var ch in value)
            {
                if (char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch > 127) escaped.Append(ch);
                else escaped.Append('\\').Append(ch);
            }
            return escaped.ToString();
        }
        var variants = new[] { term, term.ToLower(TurkishCulture), term.ToUpper(TurkishCulture) }.Distinct(StringComparer.Ordinal).Select(Escape);
        return "\"(?![A-Za-z0-9_]*[Mm][Aa][Ii][Ll]\")[A-Za-z0-9_]*\"\\s*:\\s*\"[^\"]*(?:" + string.Join('|', variants) + ")";
    }
}
