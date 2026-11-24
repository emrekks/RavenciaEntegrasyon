using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

public static partial class TrendyolJsonMapper
{
    public static AdapterPageResult<RemoteOrder> Orders(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var rows = new List<RemoteOrder>();
        var issues = new List<AdapterPageIssue>();
        foreach (var package in RequiredContent(root))
        {
            var externalPackageId = PackageId(package); var orderNumber = Text(package, "orderNumber");
            if (string.IsNullOrWhiteSpace(externalPackageId) || string.IsNullOrWhiteSpace(orderNumber))
            {
                var identity = string.IsNullOrWhiteSpace(externalPackageId) ? orderNumber : externalPackageId;
                issues.Add(RefusedOrders.Issue(identity ?? "unknown", "Sipariş paketi içeri alınmadı: paket kimliği ve sipariş numarası zorunludur.", orderNumber));
                continue;
            }
            try
            {
                // No silent fallbacks (5 Nov 2026): a package without its own order date is not dated by its last change
                // or 1970, and one without a currency is not stored with an empty one (Order.Currency is char(3), the
                // whole sync used to fail on it). Either skips this package with an issue; the order is read again.
                var ordered = OrderDate(package) ?? throw new JsonException("siparişin kendi tarihi (orderDate) yok; değişiklik tarihi sipariş tarihi sayılmaz, sipariş yeniden okunacak.");
                var currency = RefusedOrders.Currency(NullText(package, "currencyCode"))
                    ?? throw new JsonException($"para birimi (currencyCode) okunamadı ({NullText(package, "currencyCode") ?? "yok"}); sipariş yeniden okunacak.");
                var lines = new List<RemoteOrderLine>(); var allocations = new List<RemotePackageAllocation>(); var linesWithoutVat = new List<int>();
                var unreadableLines = 0;
                if (package.TryGetProperty("lines", out var lineArray) && lineArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var line in lineArray.EnumerateArray())
                    {
                        var externalLineId = "";
                        try
                        {
                            if (line.ValueKind != JsonValueKind.Object)
                                throw new JsonException("Order line is not an object.");
                            externalLineId = Text(line, "lineId", "id");
                            if (string.IsNullOrWhiteSpace(externalLineId))
                                throw new JsonException("Order line has no line ID.");
                            if (!TryDecimal(line, out var quantity, "quantity") || quantity <= 0)
                                throw new JsonException($"Order line {externalLineId} has no valid positive quantity.");
                            if (!TryDecimal(line, out var unitPrice, "lineItemPrice", "lineUnitPrice", "lineGrossAmount", "price", "amount") || unitPrice < 0)
                                throw new JsonException($"Order line {externalLineId} has no valid unit price.");
                            // vatBaseAmount is an amount, not a rate. A line without vatRate is kept (G30); its rate is taken
                            // from the package's other lines when they agree, otherwise it is reported below.
                            var hasVatRate = TryDecimal(line, out var vatRate, "vatRate");
                            if (hasVatRate && vatRate is < 0 or > 100)
                                throw new JsonException($"Order line {externalLineId} has no valid VAT rate.");
                            var barcode = NullText(line, "barcode");
                            var sku = Text(line, "stockCode", "merchantSku");
                            if (string.IsNullOrWhiteSpace(sku)) sku = barcode ?? "";
                            var title = Text(line, "productName", "title");
                            if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(title))
                                throw new JsonException($"Order line {externalLineId} has no SKU/barcode or product name.");
                            var rawStatus = Text(line, "orderLineItemStatusName");
                            if (!hasVatRate) linesWithoutVat.Add(lines.Count);
                            // lineTyDiscount is Trendyol's own discount per unit (lineUnitPrice = lineGrossAmount − lineSellerDiscount −
                            // lineTyDiscount); Trendyol pays it to the seller, so it is the seller's revenue. A line without it stays unknown.
                            decimal? platformDiscount = TryDecimal(line, out var tyDiscount, "lineTyDiscount", "tyDiscount") && tyDiscount >= 0 ? tyDiscount : null;
                            lines.Add(new(externalLineId, sku, barcode, title, quantity, unitPrice, vatRate, rawStatus, line.GetRawText(), PlatformDiscount: platformDiscount));
                            allocations.Add(new(externalLineId, quantity, 0, 0, 0, 0));
                        }
                        catch (JsonException exception)
                        {
                            // The package is kept for shipping without the line, but not silently incomplete (UnreadableOrderLines).
                            unreadableLines++;
                            issues.Add(UnreadableOrderLines.Issue("Trendyol", externalPackageId, externalLineId, exception.Message));
                        }
                    }
                    ApplyPackageVatRate(lines, linesWithoutVat, externalPackageId, issues);
                }
                if (lines.Count == 0
                    && (!package.TryGetProperty("lines", out var rawLines)
                        || rawLines.ValueKind != JsonValueKind.Array
                        || rawLines.GetArrayLength() == 0))
                {
                    // A package without its product lines is incomplete too (a split parent holds none of its own).
                    if (!MarketplaceHub.Domain.ShipmentPackageSupersession.IsSplitParentRawStatus(Text(package, "status", "shipmentPackageStatus"))) unreadableLines = Math.Max(unreadableLines, 1);
                    issues.Add(new("ORDER_PACKAGE_LINES_MISSING", externalPackageId, $"Sipariş paketi {externalPackageId} alındı ancak eşlenebilir ürün satırı bulunamadı."));
                }
                var (gross, discount, net) = PackageAmounts(package, lines);
                // Trendyol's Yeni tab is driven by the top-level package status.
                // For newly created packages shipmentPackageStatus may already be
                // ReadyToShip while status is still Created; the latter is the
                // authoritative workflow state for the order projection.
                var rawStatusPackage = Text(package, "status", "shipmentPackageStatus"); var modified = Instant(package, "lastModifiedDate") ?? ordered;
                var invoice = new RemotePackageInvoiceObservation(
                    NullText(package, "invoiceStatus"),
                    NullText(package, "invoiceNumber", "invoiceNo", "invoiceSerialNumber"),
                    NullText(package, "invoiceLink", "invoiceUrl", "invoiceDocumentUrl"),
                    FlexibleInstant(package, "invoiceUpdatedAt") ?? modified,
                    ReferencesAreAuthoritative: HasAnyProperty(package, "invoiceNumber", "invoiceNo", "invoiceSerialNumber")
                        && HasAnyProperty(package, "invoiceLink", "invoiceUrl", "invoiceDocumentUrl"));
                var remotePackage = new RemotePackage(externalPackageId, FirstArrayText(package, "originPackageIds"), rawStatusPackage, modified, OrderCargoProvider(package), ScalarText(package, "cargoTrackingNumber", "cargoSenderNumber", "trackingNumber", "trackingInfoCode"), allocations, gross, discount, net, invoice, NormalizeCreatedBy(NullText(package, "createdBy")), CancelledAt: CancelledAt(package, rawStatusPackage), UnreadableLineCount: unreadableLines);
                // The shipping deadline is the agreed hand-over date (extended when the seller extended it).
                // The estimated delivery dates are the customer's delivery window, not a deadline for the seller.
                var dueAt = FlexibleInstant(package, "extendedAgreedDeliveryDate", "agreedDeliveryDate");
                rows.Add(new(orderNumber, orderNumber, ordered, modified, currency, gross, discount, net,
                    CustomerSnapshot(package),
                    ObjectSnapshot(package, "shipmentAddress"), ObjectSnapshot(package, "invoiceAddress"), lines, [remotePackage], package.GetRawText(), dueAt));
            }
            catch (JsonException exception)
            {
                // A malformed package must not block every other package in
                // the page. It is not projected; the issue is carried with
                // the successful page for the worker to record.
                issues.Add(RefusedOrders.Issue(externalPackageId, $"Sipariş paketi {externalPackageId} (sipariş {orderNumber}) içeri alınmadı: {exception.Message}", orderNumber));
            }
        }
        var next = NullText(root, "nextCursor");
        var hasMore = Bool(root, "hasMore");
        if (string.IsNullOrWhiteSpace(next))
        {
            var currentPage = Long(root, "page");
            var totalPages = Long(root, "totalPages");
            if (totalPages > 0 && currentPage + 1 < totalPages)
            {
                next = $"p:{currentPage + 1}";
                hasMore = true;
            }
        }
        return new(rows, next, hasMore, null, issues);
    }

    // The package's own id: the first of id / shipmentPackageId / packageId that is not zero. A zero is the number Trendyol
    // gives an order awaiting payment before the package exists: it is kept as the "0" placeholder (ShipmentPackageSupersession,
    // superseded once the real package arrives) only when no field names a real id, so a zero "id" never hides a real
    // shipmentPackageId (scan 23 Nov 2026, O8). None at all is "".
    internal static string PackageId(JsonElement package)
    {
        var zero = false;
        foreach (var name in new[] { "id", "shipmentPackageId", "packageId" })
        {
            var value = NullText(package, name)?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (value.Any(character => character != '0')) return value;
            zero = true;
        }
        return zero ? MarketplaceHub.Domain.ShipmentPackageSupersession.PlaceholderExternalId : "";
    }

    // The package's sale, discount and net as Trendyol states them on the package; a total the package leaves out is
    // the sum of its lines (each line's amount, discount and price are per unit), and a net still unknown is the sale
    // less the discount. Only fields Trendyol sent are summed: a line without them leaves that total at the package's.
    // When Trendyol cancelled a package that ends cancelled (or unsupplied): the time of that step in its history, which
    // is real UTC (unlike orderDate), else its last change, since nothing changes a cancelled package after. A split
    // parent (UnPacked) is not a cancellation and has none.
    internal static DateTimeOffset? CancelledAt(JsonElement package, string rawStatus)
    {
        static bool Cancelled(string? status) => status?.Trim().Replace("_", "", StringComparison.Ordinal).ToUpperInvariant() is "CANCELLED" or "CANCELED" or "UNSUPPLIED";
        if (!Cancelled(rawStatus)) return null;
        DateTimeOffset? fromHistory = null;
        if (package.TryGetProperty("packageHistories", out var histories) && histories.ValueKind == JsonValueKind.Array)
            foreach (var entry in histories.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object && Cancelled(NullText(entry, "status")) && Instant(entry, "createdDate") is { } at && (fromHistory is null || at > fromHistory))
                    fromHistory = at;
        return fromHistory ?? Instant(package, "lastModifiedDate");
    }

    internal static (decimal Gross, decimal Discount, decimal Net) PackageAmounts(JsonElement package, IReadOnlyList<RemoteOrderLine> lines)
    {
        decimal? LineSum(params string[] names)
        {
            if (lines.Count == 0) return null;
            var sum = 0m;
            foreach (var line in lines)
            {
                using var snapshot = JsonDocument.Parse(line.SourceSnapshotJson);
                if (!TryDecimal(snapshot.RootElement, out var value, names)) return null;
                sum += value * line.Quantity;
            }
            return sum;
        }

        var gross = TryDecimal(package, out var packageGross, "packageGrossAmount", "grossAmount")
            ? packageGross
            : LineSum("amount", "lineGrossAmount") ?? Decimal(package, "packageTotalPrice");
        decimal discount;
        if (TryDecimal(package, out var packageDiscount, "packageTotalDiscount") && packageDiscount != 0)
            discount = packageDiscount;
        else if (HasAnyProperty(package, "packageTotalDiscount", "packageSellerDiscount", "totalDiscount", "packageTyDiscount", "totalTyDiscount"))
            discount = Decimal(package, "packageSellerDiscount", "totalDiscount") + Decimal(package, "packageTyDiscount", "totalTyDiscount");
        else
            discount = LineSum("discount", "lineTotalDiscount") ?? 0m;
        var net = TryDecimal(package, out var packageNet, "packageTotalPrice", "totalPrice")
            ? packageNet
            : Math.Max(0m, gross - discount);
        return (gross, discount, net);
    }

    // G30: a line without vatRate keeps its place in the package. When every line that has a rate agrees, that is the
    // package's rate and the line takes it (reported as inferred); otherwise the line carries 0 and is reported. The order,
    // its stock and the package allocation stay whole, but no invoice is issued for the order until Trendyol reports the
    // rate (InvoiceIssuePolicy.IsLineVatRateUnknown reads the line's raw JSON).
    private static void ApplyPackageVatRate(List<RemoteOrderLine> lines, List<int> linesWithoutVat, string externalPackageId, List<AdapterPageIssue> issues)
    {
        if (linesWithoutVat.Count == 0) return;
        var missing = linesWithoutVat.ToHashSet();
        var known = lines.Where((_, index) => !missing.Contains(index)).Select(line => line.VatRate.GetValueOrDefault()).Distinct().ToList();
        foreach (var index in linesWithoutVat)
        {
            var line = lines[index];
            if (known.Count == 1)
            {
                lines[index] = line with { VatRate = known[0] };
                issues.Add(new("ORDER_PACKAGE_LINE_VAT_RATE_INFERRED", $"{externalPackageId}:{line.ExternalLineId}",
                    $"Sipariş paketi {externalPackageId} satırında KDV oranı yoktu; paketin diğer satırlarındaki %{known[0].ToString("0.##", CultureInfo.InvariantCulture)} kullanıldı. Oran Trendyol'dan gelmeden bu siparişe fatura kesilmez."));
            }
            else
            {
                issues.Add(new("ORDER_PACKAGE_LINE_VAT_RATE_MISSING", $"{externalPackageId}:{line.ExternalLineId}",
                    $"Sipariş paketi {externalPackageId} satırında KDV oranı yok; satır alındı; oran Trendyol'dan gelmeden bu siparişe fatura kesilmez."));
            }
        }
    }

    public static RemoteOrder? MergeOrderPackages(IEnumerable<RemoteOrder> candidates, string externalOrderId)
    {
        var matches = candidates
            .Where(x => string.Equals(x.ExternalOrderId, externalOrderId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.OrderNumber, externalOrderId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return null;

        var latest = matches.OrderByDescending(x => x.LastModifiedAt).First();
        var packageCandidates = matches
            .SelectMany(match => match.Packages.Select(package => new { Match = match, Package = package }))
            .GroupBy(x => x.Package.ExternalPackageId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(x => x.Package.OccurredAt)
                .ThenByDescending(x => x.Match.LastModifiedAt)
                .First())
            .ToList();
        var packages = packageCandidates.Select(x => x.Package).ToList();
        var replacedPackageIds = packages
            .Where(package => !string.IsNullOrWhiteSpace(package.OriginExternalPackageId)
                && packages.Any(candidate => string.Equals(candidate.ExternalPackageId, package.OriginExternalPackageId, StringComparison.Ordinal)))
            .Select(package => package.OriginExternalPackageId!)
            .ToHashSet(StringComparer.Ordinal);
        var currentPackageCandidates = packageCandidates
            .Where(candidate => !replacedPackageIds.Contains(candidate.Package.ExternalPackageId))
            .ToList();
        var lines = currentPackageCandidates
            .SelectMany(candidate => candidate.Match.Lines.Select(line => new { candidate, line }))
            .GroupBy(x => x.line.ExternalLineId, StringComparer.Ordinal)
            .Select(group =>
            {
                var metadata = group
                    .OrderByDescending(x => x.candidate.Package.OccurredAt)
                    .ThenByDescending(x => x.candidate.Match.LastModifiedAt)
                    .Select(x => x.line)
                    .First();
                var quantity = group.Sum(x => x.candidate.Package.Allocations
                    .Where(allocation => string.Equals(allocation.ExternalLineId, x.line.ExternalLineId, StringComparison.Ordinal))
                    // A line's ordered quantity is what its packages hold active plus what they cancelled. Trendyol never
                    // reports a cancelled quantity here (0); PttAVM and Pazarama allocate a cancelled line as 0 active and
                    // its quantity cancelled, and counting only the active part made it 0 ordered with 1 cancelled, which
                    // the package check refused (audit 14 Nov 2026: PTT-TEST00002-120926 was never imported).
                    .Select(allocation => allocation.AllocatedQuantity + allocation.CancelledQuantity)
                    .DefaultIfEmpty(x.line.Quantity)
                    .Sum());
                return metadata with { Quantity = quantity };
            })
            .ToList();

        // A package split off another replaces it, so the replaced package stays in the list (its status is
        // still tracked) but no longer counts towards the order totals.
        var currentPackages = currentPackageCandidates.Select(x => x.Package).ToList();
        return latest with
        {
            LastModifiedAt = matches.Max(x => x.LastModifiedAt),
            GrossAmount = currentPackages.Sum(x => x.GrossAmount),
            DiscountAmount = currentPackages.Sum(x => x.DiscountAmount),
            NetAmount = currentPackages.Sum(x => x.NetAmount),
            Lines = lines,
            Packages = packages
        };
    }

    public static RemoteOrderPackage? ShipmentPackage(string json, string externalPackageId) =>
        string.IsNullOrWhiteSpace(externalPackageId) ? null : ShipmentPackage(Orders(json), externalPackageId);

    // The package from an already mapped page, so a page that is also paged on is parsed once.
    public static RemoteOrderPackage? ShipmentPackage(AdapterPageResult<RemoteOrder> page, string externalPackageId)
    {
        if (string.IsNullOrWhiteSpace(externalPackageId)) return null;
        var order = page.Items.FirstOrDefault(candidate =>
            candidate.Packages.Any(package => string.Equals(package.ExternalPackageId, externalPackageId, StringComparison.Ordinal)));
        if (order is null) return null;
        var package = order.Packages.FirstOrDefault(candidate =>
            string.Equals(candidate.ExternalPackageId, externalPackageId, StringComparison.Ordinal));
        return package is null ? null : new(order.ExternalOrderId, package, order);
    }

    public static RemoteOrderPackage? PreferShipmentPackageWithCargo(RemoteOrderPackage? preferredRead, RemoteOrderPackage? fallbackRead)
    {
        var preferred = HasCargo(preferredRead) ? preferredRead : HasCargo(fallbackRead) ? fallbackRead : fallbackRead ?? preferredRead;
        var secondary = ReferenceEquals(preferred, preferredRead) ? fallbackRead : preferredRead;
        if (preferred is null || secondary is null) return preferred;

        var cargoProvider = string.IsNullOrWhiteSpace(preferred.Package.CargoProviderExternalId)
            ? secondary.Package.CargoProviderExternalId
            : preferred.Package.CargoProviderExternalId;
        var cargoTrackingNumber = string.IsNullOrWhiteSpace(preferred.Package.CargoTrackingNumber)
            ? secondary.Package.CargoTrackingNumber
            : preferred.Package.CargoTrackingNumber;
        var invoice = PreferInvoiceObservation(preferred.Package.Invoice, secondary.Package.Invoice);
        var orderSnapshot = PreferOrderSnapshot(preferred.OrderSnapshot, secondary.OrderSnapshot);
        if (cargoProvider == preferred.Package.CargoProviderExternalId
            && cargoTrackingNumber == preferred.Package.CargoTrackingNumber
            && invoice == preferred.Package.Invoice
            && orderSnapshot == preferred.OrderSnapshot) return preferred;

        return preferred with
        {
            Package = preferred.Package with
            {
                CargoProviderExternalId = cargoProvider,
                CargoTrackingNumber = cargoTrackingNumber,
                Invoice = invoice
            },
            OrderSnapshot = orderSnapshot
        };
    }

    private static bool HasCargo(RemoteOrderPackage? package) =>
        !string.IsNullOrWhiteSpace(package?.Package.CargoProviderExternalId)
        || !string.IsNullOrWhiteSpace(package?.Package.CargoTrackingNumber);

    private static RemotePackageInvoiceObservation? PreferInvoiceObservation(
        RemotePackageInvoiceObservation? first,
        RemotePackageInvoiceObservation? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        var firstHasObservation = HasInvoiceObservation(first);
        var secondHasObservation = HasInvoiceObservation(second);
        var preferSecond = !firstHasObservation && secondHasObservation
            || firstHasObservation && secondHasObservation
                && second.SourceUpdatedAt is { } secondUpdatedAt
                && (first.SourceUpdatedAt is null || secondUpdatedAt > first.SourceUpdatedAt.Value);
        var preferred = preferSecond ? second : first;
        var other = ReferenceEquals(preferred, first) ? second : first;
        return preferred with
        {
            RawStatus = FirstText(preferred.RawStatus, other.RawStatus),
            // References belong to the selected observation. Backfilling a
            // missing field from an older payload makes a removed/replaced
            // platform link look current.
            InvoiceNumber = preferred.InvoiceNumber,
            InvoiceUrl = preferred.InvoiceUrl,
            SourceUpdatedAt = preferred.SourceUpdatedAt ?? other.SourceUpdatedAt
        };
    }

    private static string? FirstText(string? preferred, string? other) =>
        string.IsNullOrWhiteSpace(preferred) ? other : preferred;

    // The invoice fields Trendyol sent: a status, a number or a link.
    internal static bool HasInvoiceValue(RemotePackageInvoiceObservation? invoice) => invoice is not null
        && (!string.IsNullOrWhiteSpace(invoice.RawStatus)
            || !string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            || !string.IsNullOrWhiteSpace(invoice.InvoiceUrl));

    // An invoice value, or an authoritative statement that the package has no invoice references (which must
    // still win over an older observation that had them).
    internal static bool HasInvoiceObservation(RemotePackageInvoiceObservation? invoice) =>
        HasInvoiceValue(invoice) || invoice?.ReferencesAreAuthoritative == true;

    private static bool HasAnyProperty(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Any(property => names.Contains(property.Name, StringComparer.OrdinalIgnoreCase));

    private static RemoteOrder? PreferOrderSnapshot(RemoteOrder? first, RemoteOrder? second) =>
        first is null ? second
            : second is null || first.LastModifiedAt >= second.LastModifiedAt ? first
            : second;

    // An order or claim page always carries its content list (empty when the window holds nothing). A successful answer
    // without it is a broken contract, never "no orders": the callers turn the JsonException into a contract error, so
    // the sync stops visibly instead of moving its window past orders it never saw.
    private static IEnumerable<JsonElement> RequiredContent(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
            : throw new JsonException("Trendyol page has no content list.");

    private static string? FirstArrayText(JsonElement value, string field) => value.TryGetProperty(field, out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0 ? array[0].ToString() : null;
    private static string? OrderCargoProvider(JsonElement package)
    {
        var nestedProvider = NestedText(package, "cargoProvider", "name", "shortName", "code", "label");
        return !string.IsNullOrWhiteSpace(nestedProvider)
            ? nestedProvider
            : ScalarText(package, "cargoProviderName", "cargoProviderCode", "cargoProviderId", "cargoProvider");
    }
}
