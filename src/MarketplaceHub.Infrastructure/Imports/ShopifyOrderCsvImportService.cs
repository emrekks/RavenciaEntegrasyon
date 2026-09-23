using System.Globalization;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Imports;

public sealed class ShopifyOrderCsvImportService(AppDbContext db, TimeProvider timeProvider) : IShopifyOrderCsvImportService
{
    private const int MaximumBytes = 10 * 1024 * 1024;
    private const int MaximumReportedItems = 25;

    public async Task<ServiceResult<ShopifyOrderCsvImportResult>> ImportAsync(
        Guid tenantId,
        Guid actorUserId,
        Guid? connectionId,
        Stream csv,
        string correlationId,
        CancellationToken cancellationToken)
    {
        string contents;
        try
        {
            using var reader = new StreamReader(csv, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var buffer = new char[8192];
            var text = new StringBuilder();
            var charLimit = MaximumBytes;
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) break;
                if (text.Length + count > charLimit)
                    return ServiceResult<ShopifyOrderCsvImportResult>.Fail("SHOPIFY_CSV_TOO_LARGE", "Shopify CSV dosyası en fazla 10 MiB olabilir.", 413);
                text.Append(buffer, 0, count);
            }
            contents = text.ToString();
        }
        catch (DecoderFallbackException)
        {
            return ServiceResult<ShopifyOrderCsvImportResult>.Fail("SHOPIFY_CSV_ENCODING_INVALID", "Dosya UTF-8 CSV biçiminde okunamadı.", 422);
        }

        ShopifyOrderCsvParseResult parsed;
        try { parsed = ShopifyOrderCsvParser.Parse(new StringReader(contents)); }
        catch (FormatException exception)
        {
            return ServiceResult<ShopifyOrderCsvImportResult>.Fail("SHOPIFY_CSV_INVALID", exception.Message, 422);
        }

        var invalidOrders = parsed.Orders.Where(order => order.ValidationIssue is not null).ToArray();
        var validOrders = parsed.Orders.Where(order => order.ValidationIssue is null).ToArray();
        if (parsed.Orders.Count == 0)
            return ServiceResult<ShopifyOrderCsvImportResult>.Fail("SHOPIFY_CSV_NO_ORDERS", "CSV dosyasında sipariş numarası olan bir sipariş satırı bulunamadı.", 422);
        var issues = invalidOrders.Select(order => $"{order.OrderNumber}: {order.ValidationIssue}").Take(MaximumReportedItems).ToList();
        var unmatchedOrderNumbers = new List<string>();
        var unmatchedOrders = new List<ShopifyOrderCsvOrder>();
        var matchedCount = 0;
        var updatedCount = 0;
        var updatedLineCount = 0;
        var unmatchedLineCount = 0;
        var ambiguousCount = 0;
        var now = timeProvider.GetUtcNow();

        var connectionQuery = db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && connection.PlatformCode == "SHOPIFY");
        if (connectionId is not null) connectionQuery = connectionQuery.Where(connection => connection.Id == connectionId.Value);
        var connectionIds = await connectionQuery.Select(connection => connection.Id).ToListAsync(cancellationToken);
        if (connectionIds.Count == 0)
            return ServiceResult<ShopifyOrderCsvImportResult>.Fail(
                connectionId is null ? "SHOPIFY_CONNECTION_NOT_FOUND" : "SHOPIFY_CONNECTION_INVALID",
                connectionId is null ? "Bu işletmeye ait Shopify bağlantısı bulunamadı." : "Seçilen bağlantı bu işletmeye ait bir Shopify bağlantısı değil.",
                connectionId is null ? 404 : 422);

        var orders = await db.Orders
            .Where(order => order.TenantId == tenantId && connectionIds.Contains(order.ConnectionId))
            .ToListAsync(cancellationToken);
        var ordersByNumber = orders
            .GroupBy(order => ShopifyOrderCsvParser.NormalizeOrderNumber(order.OrderNumber), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var orderMatches = new List<(ShopifyOrderCsvOrder Import, Order Existing)>();
        foreach (var imported in validOrders)
        {
            var key = ShopifyOrderCsvParser.NormalizeOrderNumber(imported.OrderNumber);
            if (!ordersByNumber.TryGetValue(key, out var matches))
            {
                unmatchedOrderNumbers.Add(imported.OrderNumber);
                unmatchedOrders.Add(imported);
                continue;
            }
            if (matches.Length != 1)
            {
                ambiguousCount++;
                if (issues.Count < MaximumReportedItems) issues.Add($"{imported.OrderNumber}: Birden fazla Shopify siparişiyle eşleşti; güvenlik için atlandı.");
                continue;
            }
            orderMatches.Add((imported, matches[0]));
        }

        matchedCount = orderMatches.Count;
        if (orderMatches.Count > 0)
        {
            var orderIds = orderMatches.Select(match => match.Existing.Id).ToArray();
            var linesByOrder = (await db.OrderLines
                    .Where(line => line.TenantId == tenantId && orderIds.Contains(line.OrderId))
                    .ToListAsync(cancellationToken))
                .GroupBy(line => line.OrderId)
                .ToDictionary(group => group.Key, group => group.ToList());
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            foreach (var (imported, order) in orderMatches)
            {
                var changed = ApplyOrder(order, imported, now);
                if (!linesByOrder.TryGetValue(order.Id, out var existingLines)) existingLines = [];
                var unusedLines = existingLines.ToHashSet();
                foreach (var importedLine in imported.Lines)
                {
                    var line = FindLine(unusedLines, importedLine);
                    if (line is null) { unmatchedLineCount++; continue; }
                    unusedLines.Remove(line);
                    var lineChanged = false;
                    if (!string.IsNullOrWhiteSpace(importedLine.Sku) && line.Sku != importedLine.Sku) { line.Sku = importedLine.Sku; lineChanged = true; }
                    if (line.TitleSnapshot != importedLine.Title) { line.TitleSnapshot = importedLine.Title; lineChanged = true; }
                    if (importedLine.UnitPrice is { } price && line.UnitPrice != price) { line.UnitPrice = price; lineChanged = true; }
                    if (lineChanged || !ShopifyOrderCsvSnapshotPolicy.HasImport(line.SourceSnapshotJson))
                    {
                        line.SourceSnapshotJson = ShopifyOrderCsvSnapshotPolicy.ImportLine(line.SourceSnapshotJson, importedLine, now);
                        line.Version++;
                        lineChanged = true;
                    }
                    if (lineChanged) updatedLineCount++;
                }

                if (changed)
                {
                    order.UpdatedAt = now;
                    order.Version++;
                    updatedCount++;
                }
            }

            if (updatedCount > 0 || updatedLineCount > 0)
            {
                db.AuditLogs.Add(new AuditLog
                {
                    TenantId = tenantId,
                    ActorUserId = actorUserId,
                    Action = "SHOPIFY_ORDER_CSV_IMPORT",
                    TargetType = "ShopifyOrderCsvImport",
                    Reason = $"orders={parsed.Orders.Count};matched={matchedCount};updated={updatedCount};lines={updatedLineCount};unmatched={unmatchedOrderNumbers.Count + invalidOrders.Length}",
                    CorrelationId = correlationId,
                    CreatedAt = now
                });
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else await transaction.RollbackAsync(cancellationToken);
        }

        unmatchedOrderNumbers.AddRange(invalidOrders.Select(order => order.OrderNumber));
        var historicalUnmatchedCount = ShopifyOrderHistoryPolicy.CountOrdersRequiringAllOrdersScope(
            unmatchedOrders.Select(order => order.CreatedAt),
            now);
        if (historicalUnmatchedCount > 0 && issues.Count < MaximumReportedItems)
            issues.Add(ShopifyOrderHistoryPolicy.HistoricalOrdersScopeNotice(historicalUnmatchedCount));
        if (parsed.SkippedRows > 0 && issues.Count < MaximumReportedItems)
            issues.Add($"{parsed.SkippedRows} boş veya sipariş numarası olmayan satır atlandı.");
        if (unmatchedLineCount > 0 && issues.Count < MaximumReportedItems)
            issues.Add($"{unmatchedLineCount} ürün satırı mevcut sipariş satırlarıyla güvenle eşleşmedi.");

        var result = new ShopifyOrderCsvImportResult(
            parsed.FileRows,
            parsed.Orders.Count,
            matchedCount,
            updatedCount,
            updatedLineCount,
            unmatchedOrderNumbers.Count,
            ambiguousCount,
            unmatchedLineCount,
            unmatchedOrderNumbers.Take(MaximumReportedItems).ToArray(),
            issues.Take(MaximumReportedItems).ToArray());
        return ServiceResult<ShopifyOrderCsvImportResult>.Ok(result);
    }

    private static bool ApplyOrder(Order order, ShopifyOrderCsvOrder imported, DateTimeOffset now)
    {
        var changed = false;
        var customerName = imported.BillingAddress.Name ?? imported.ShippingAddress.Name;
        var customerPhone = imported.Phone ?? imported.BillingAddress.Phone ?? imported.ShippingAddress.Phone;
        var netTotal = imported.Total ?? imported.Subtotal;
        var grossTotal = netTotal is { } parsedNetTotal ? parsedNetTotal + (imported.DiscountAmount ?? 0m) : (decimal?)null;
        var customerJson = ShopifyOrderCsvSnapshotPolicy.ImportCustomer(
            order.CustomerSnapshotJson, customerName, imported.Email, customerPhone,
            imported.FinancialStatus, imported.FulfillmentStatus, imported.PaymentMethod,
            grossTotal, netTotal, imported.DiscountAmount, imported.Currency, now);
        if (customerJson != order.CustomerSnapshotJson) { order.CustomerSnapshotJson = customerJson; changed = true; }

        var shippingJson = ShopifyOrderCsvSnapshotPolicy.ImportAddress(order.ShipmentAddressSnapshotJson, imported.ShippingAddress);
        if (shippingJson != order.ShipmentAddressSnapshotJson) { order.ShipmentAddressSnapshotJson = shippingJson; changed = true; }
        var billingJson = ShopifyOrderCsvSnapshotPolicy.ImportAddress(order.InvoiceAddressSnapshotJson, imported.BillingAddress);
        if (billingJson != order.InvoiceAddressSnapshotJson) { order.InvoiceAddressSnapshotJson = billingJson; changed = true; }

        if (!string.IsNullOrWhiteSpace(imported.Currency) && order.Currency != imported.Currency) { order.Currency = imported.Currency; changed = true; }
        if (grossTotal is { } grossAmount)
        {
            if (order.GrossAmount != grossAmount) { order.GrossAmount = grossAmount; changed = true; }
        }
        if (netTotal is { } netAmount && order.NetAmount != netAmount) { order.NetAmount = netAmount; changed = true; }
        if (imported.DiscountAmount is { } discount && order.DiscountAmount != discount) { order.DiscountAmount = discount; changed = true; }
        return changed;
    }

    private static OrderLine? FindLine(HashSet<OrderLine> candidates, ShopifyOrderCsvLine imported)
    {
        var skuMatches = !string.IsNullOrWhiteSpace(imported.Sku)
            ? candidates.Where(line => string.Equals(Normalize(line.Sku), Normalize(imported.Sku), StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        if (skuMatches.Length == 1) return skuMatches[0];
        if (skuMatches.Length > 1)
        {
            var exactTitle = skuMatches.Where(line => string.Equals(Normalize(line.TitleSnapshot), Normalize(imported.Title), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exactTitle.Length > 0) return exactTitle[0];
            return skuMatches.OrderBy(line => Math.Abs(line.UnitPrice - (imported.UnitPrice ?? line.UnitPrice))).First();
        }
        var titleMatches = candidates.Where(line => string.Equals(Normalize(line.TitleSnapshot), Normalize(imported.Title), StringComparison.OrdinalIgnoreCase)).ToArray();
        return titleMatches.Length == 1 ? titleMatches[0] : titleMatches.Length > 1
            ? titleMatches.OrderBy(line => Math.Abs(line.UnitPrice - (imported.UnitPrice ?? line.UnitPrice))).First()
            : null;
    }

    private static string Normalize(string? value) => string.Join(' ', (value ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
