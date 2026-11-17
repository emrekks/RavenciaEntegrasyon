using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests;

// SIPARIS-STOK-9: the Returns page's search is decided by the database on the columns it shows (ReturnSearchInDatabase)
// and from the view only for the customer name and a barcode the catalog lends. The database's text rule must equal
// MatchesReturnSearch's folding on every kind of character, and its column match must be exactly the in-memory match
// of the same fields, so a claim the database matches is always a match and the view decides only the rest.
[Collection("postgres-integration")]
public sealed class ReturnSearchPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // Characters that break naive folding or regular expressions: the four i letters, Turkish capitals, the Kelvin sign,
    // sharp s, final sigma, a title-case digraph, supplementary letters (Deseret), full-width letters, regex and LIKE
    // specials, white space.
    private static readonly string[] Pieces =
    [
        "İ", "I", "ı", "i", "Ş", "ş", "Ğ", "ğ", "Ü", "ü", "Ö", "ö", "Ç", "ç", "K", "k", "K", "ẞ", "ß", "Σ", "σ", "ς", "ǅ", "ǆ",
        "\U00010400", "\U00010428", "Ａ", "ａ", "%", "_", "\\", "[", "]", "^", "$", ".", "*", "(", ")", "|", "?", "+", "{", "}", "-", " ", "\t", " ", "—",
        "0", "7", "a", "A", "e", "E", "😀"
    ];

    [PostgreSqlFact]
    public async Task The_database_search_rule_equals_the_in_memory_folding()
    {
        var random = new Random(20261116);
        string Random(int length) => string.Concat(Enumerable.Range(0, length).Select(_ => Pieces[random.Next(Pieces.Length)]));
        var values = new List<string>();
        var terms = new List<string>();
        for (var index = 0; index < 4000; index++)
        {
            var value = Random(random.Next(1, 12));
            values.Add(value);
            // A term cut from the value, its case changed the other way, or a fresh one.
            var term = random.Next(3) switch
            {
                0 => value.Substring(random.Next(value.Length), 1),
                1 => random.Next(2) == 0 ? value.ToUpperInvariant() : value.ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR")),
                _ => Random(random.Next(1, 4))
            };
            terms.Add(term);
        }
        // Folding is one code point to one code point: a value folds as its code points do.
        foreach (var value in values)
        {
            var runes = new StringBuilder();
            foreach (var rune in value.EnumerateRunes()) runes.Append(ReturnSearchText.Fold(rune.ToString()));
            Assert.Equal(ReturnSearchText.Fold(value), runes.ToString());
        }

        var needles = values.Zip(terms).Select(pair => (Value: pair.First, Needle: ReturnSearchText.Needle(pair.Second))).Where(pair => pair.Needle.Length > 0).ToList();
        // A term cut through a surrogate pair cannot be sent: it has no pattern (the scan decides), and nothing else lacks one.
        Assert.All(needles.Where(pair => ReturnSearchText.SearchPattern(pair.Needle) is null), pair => Assert.True(pair.Needle.EnumerateRunes().Any(rune => rune == Rune.ReplacementChar), Escape(pair.Needle)));
        var pairs = needles.Where(pair => ReturnSearchText.SearchPattern(pair.Needle) is not null).ToList();
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION"));
        await connection.OpenAsync();
        // As the provider sends Regex.IsMatch: the pattern behind the (?p) option.
        await using var command = new NpgsqlCommand("SELECT v ~ ('(?p)' || p) FROM unnest(@values, @patterns) WITH ORDINALITY AS t(v, p, n) ORDER BY n", connection);
        command.Parameters.AddWithValue("values", pairs.Select(pair => pair.Value).ToArray());
        var patterns = pairs.Select(pair => ReturnSearchText.SearchPattern(pair.Needle)).ToArray();
        command.Parameters.AddWithValue("patterns", patterns);
        var matched = new List<bool>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) matched.Add(reader.GetBoolean(0));
        Assert.Equal(pairs.Count, matched.Count);
        var hits = 0;
        for (var index = 0; index < pairs.Count; index++)
        {
            var expected = ReturnSearchText.Fold(pairs[index].Value).Contains(pairs[index].Needle, StringComparison.Ordinal);
            Assert.True(expected == matched[index], $"{Escape(pairs[index].Value)} / {Escape(pairs[index].Needle)}: {expected} expected");
            if (expected) hits++;
        }
        Assert.True(hits > 500 && hits < pairs.Count - 500, $"{hits} of {pairs.Count} matched");
        output.WriteLine($"{pairs.Count} values compared, {hits} matches");

        // A term that cannot be sent (a lone surrogate, a NUL) has no pattern: the scan decides, as before.
        Assert.Null(ReturnSearchText.SearchPattern("a\uD800"));
        Assert.Null(ReturnSearchText.SearchPattern("a\0"));
    }

    // The database's column match equals the in-memory match of the same fields (claim id, reason code and text, order
    // number, every line's SKU, barcode and title) for every term, and never says yes where the page's matcher says no.
    [PostgreSqlFact]
    public async Task The_column_match_is_exactly_the_in_memory_match_of_those_fields()
    {
        var tenantId = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);
            var claims = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync();
            var orders = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.Id);
            var lines = (await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync()).ToLookup(x => x.ClaimId);
            var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.Id);
            var views = await service.ReturnQueries.ReturnViewsByIdAsync(tenantId, [.. claims.Select(x => x.Id)], CancellationToken.None);
            var compared = 0;
            foreach (var term in Terms)
            {
                var search = ReturnQueryService.ReturnSearchTermOf(term)!;
                var needle = ReturnSearchText.Needle(term);
                bool Has(string? value) => value is not null && ReturnSearchText.Fold(value).Contains(needle, StringComparison.Ordinal);
                var expected = claims.Where(claim => Has(claim.ExternalClaimId) || Has(claim.ReasonText) || Has(claim.ReasonCode)
                        || (orders.TryGetValue(claim.OrderId, out var order) ? Has(order.OrderNumber) : Has("—"))
                        || lines[claim.Id].Any(line => orderLines.TryGetValue(line.OrderLineId, out var orderLine) && (Has(orderLine.Sku) || Has(orderLine.Barcode) || Has(orderLine.TitleSnapshot))))
                    .Select(claim => claim.Id).ToHashSet();
                var actual = (await db.ReturnClaims.Where(x => x.TenantId == tenantId).Where(service.ReturnQueries.ReturnSearchInDatabase(tenantId, search)).Select(x => x.Id).ToListAsync()).ToHashSet();
                Assert.True(expected.SetEquals(actual), $"'{term}': {expected.Count} expected, {actual.Count} matched");
                // Sound: whatever the columns match, the page's matcher matches on the whole card.
                foreach (var id in actual)
                    Assert.True(ReturnQueryService.MatchesReturnSearch(views[id], [.. (views[id].Lines ?? []).Select(line => line.Id)], term), $"'{term}': {id}");
                compared += claims.Count;
            }
            output.WriteLine($"{compared} claim and term pairs compared");
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // The page and summary for each term equal the card-by-card reference, and each kind of term finds what it must:
    // a line only, the order number only, the customer only.
    [PostgreSqlFact]
    public async Task Search_pages_and_summaries_equal_the_in_memory_ones()
    {
        var tenantId = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var cursors = new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System);
            var service = NewService(db, cursors);
            var compared = 0;
            foreach (var term in Terms)
                foreach (var tab in new string?[] { null, "APPROVED", "ACTION_REQUIRED", "REQUESTED" })
                {
                    var options = new ReturnListQuery(Search: term, Status: tab);
                    compared += await ReturnInvoiceLabelPostgreSqlTests.ComparePagesAsync(service, db, cursors, tenantId, 3, options);
                    var expected = await LegacyReturnListQueries.BuildReturnSummaryAsync(service, db, tenantId, options, CancellationToken.None);
                    var actual = await service.ReturnQueries.BuildReturnSummaryAsync(tenantId, options, ReturnQueryService.ReturnTabs.ToDictionary(x => x, _ => 0, StringComparer.Ordinal), CancellationToken.None);
                    Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), System.Text.Json.JsonSerializer.Serialize(actual));
                }
            foreach (var (term, what) in new[] { ("Kırmızı kazak%_", "a line"), ("ORD-77001", "the order number"), ("Şükrü", "the customer"), ("Çağlayan", "the customer"), ("SKU-ONLY-9", "a line's SKU") })
                Assert.True((await service.ReturnQueries.ReturnCardPageAsync(tenantId, 50, 1, null, new ReturnListQuery(Search: term), CancellationToken.None)).TotalCount > 0, $"'{term}' finds no claim by {what}");
            output.WriteLine($"{compared} pages compared");

            // SIPARIS-STOK-9: every order keeps its customer name; a snapshot written outside the tracker empties it (the
            // trigger), and the search still equals the reference (the view decides that order) until the backfill.
            Assert.False(await db.Orders.AnyAsync(x => x.TenantId == tenantId && x.CustomerSearchName == null));
            // A context that read the order before the snapshot write keeps the old snapshot in memory.
            await using var stale = fixture.CreateContext();
            var staleOrder = await stale.Orders.SingleAsync(x => x.TenantId == tenantId && x.OrderNumber == "S-1009");
            await db.Orders.Where(x => x.TenantId == tenantId && x.OrderNumber == "S-1009")
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.CustomerSnapshotJson, """{"customerFirstName":"Nurşen","customerLastName":"Kaya"}"""));
            Assert.Null(await db.Orders.Where(x => x.TenantId == tenantId && x.OrderNumber == "S-1009").Select(x => x.CustomerSearchName).SingleAsync());
            // Its save of another field writes no name from that old snapshot: the name stays empty for the backfill.
            staleOrder.DerivedStatus = "SHIPPED";
            await stale.SaveChangesAsync();
            Assert.Null(await db.Orders.Where(x => x.TenantId == tenantId && x.OrderNumber == "S-1009").Select(x => x.CustomerSearchName).SingleAsync());
            await db.Orders.Where(x => x.TenantId == tenantId && x.OrderNumber == "S-1009").ExecuteUpdateAsync(set => set.SetProperty(x => x.DerivedStatus, "DELIVERED"));
            foreach (var term in new[] { "nurşen", "Şükrü", "a" })
            {
                var options = new ReturnListQuery(Search: term);
                await ReturnInvoiceLabelPostgreSqlTests.ComparePagesAsync(service, db, cursors, tenantId, 3, options);
            }
            Assert.True((await service.ReturnQueries.ReturnCardPageAsync(tenantId, 50, 1, null, new ReturnListQuery(Search: "nurşen"), CancellationToken.None)).TotalCount > 0);
            Assert.True(await OrderCustomerSearchNames.BackfillAsync(db, CancellationToken.None) >= 1);
            Assert.Equal("Nurşen Kaya", await db.Orders.Where(x => x.TenantId == tenantId && x.OrderNumber == "S-1009").Select(x => x.CustomerSearchName).SingleAsync());
            await ReturnInvoiceLabelPostgreSqlTests.ComparePagesAsync(service, db, cursors, tenantId, 3, new ReturnListQuery(Search: "nurşen"));
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    private static readonly string[] Terms =
    [
        "a", "IŞIK", "ışık", "işık", "ISIK", "isik", "İSTANBUL", "istanbul", "1003", "77001", "  ord-77001  ", "\tORD-77001　", "kırmızı kazak", "Kırmızı kazak%_", "%", "_", "%_", "\\",
        "ürün 2", "ÜRÜN", "beden küçük", "BEDEN KÜÇÜK", "şükrü", "Şükrü", "ŞÜKRÜ ÇAĞLAYAN", "çağlayan", "Çağlayan", "SKU-ONLY-9", "sku-only", "868", "7700lent", "7700LENT1", "LENT2", "gülşen", "GÜLŞEN ÖZTÜRK", "öztürk", "—", "Ş", "zzz-yok", "rc-", "RC-2", "Σ", "ς", "K", "k"
    ];

    private static string Escape(string value) => string.Join(" ", value.EnumerateRunes().Select(rune => $"U+{rune.Value:X4}"));

    private static MarketplaceSalesService NewService(AppDbContext db, CursorCodec? cursors = null) =>
        new(db, cursors ?? new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);

    // Claims whose fields carry Turkish letters in both cases, digits, white space, LIKE specials; one claim is found only
    // by a line title, one only by its order number, one only by its customer, one only by a SKU; some split.
    private async Task<Guid> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"return-search-{Guid.NewGuid():N}", DisplayName = "return-search", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol Mağaza", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        string[] customers =
        [
            """{"customerFirstName":"Şükrü","customerLastName":"Çağlayan"}""", """{"customerFirstName":"IŞIK","customerLastName":"İSTANBUL"}""", """{"customerFirstName":"ışık"}""",
            """{"customerFirstName":"Ayşe","customerLastName":"Yılmaz"}""", "{}"
        ];
        string[] titles = ["Ürün", "ÜRÜN İPEK", "Kırmızı Kazak%_", "beden_küçük", "Işıklı Lamba", "Σίσυφος", "Kelvin"];
        (string? Code, string? Text)[] reasons = [("SIZE", "Beden küçük"), (null, "BEDEN KÜÇÜK"), ("RC-2", null), (null, null), ("Işık", "ışık 100%")];
        // Catalog variants that lend a barcode to lines without one: by the line's own VariantId (V1), and by SKU (V2).
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Katalog", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var lendById = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = "CAT-LENT-1", SkuNormalized = "CAT-LENT-1", Barcode = "7700LENT1", BarcodeNormalized = "7700LENT1", OptionSignature = "Beden: S", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var lendBySku = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = "S4-1", SkuNormalized = "S4-1", Barcode = "7700LENT2", BarcodeNormalized = "7700LENT2", OptionSignature = "Beden: M", Status = ProductStatus.Active, SortOrder = 1, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var orders = new List<Order>();
        var orderLines = new List<OrderLine>();
        var claims = new List<ReturnClaim>();
        var returnLines = new List<ReturnLine>();
        for (var i = 0; i < 30; i++)
        {
            var orderNumber = i == 7 ? "ORD-77001" : $"S-{1000 + i}";
            var order = new Order
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                ExternalOrderId = $"S-{i}",
                OrderNumber = orderNumber,
                Currency = "TRY",
                GrossAmount = 100m,
                NetAmount = 100m,
                OrderedAt = now.AddDays(-(i % 5)),
                LastRemoteModifiedAt = now,
                CustomerSnapshotJson = i == 9 ? customers[0] : i == 13 ? "{}" : customers[1 + i % (customers.Length - 1)],
                ShipmentAddressSnapshotJson = "{}",
                // A name only the invoice address gives.
                InvoiceAddressSnapshotJson = i == 13 ? """{"firstName":"Gülşen","lastName":"Öztürk"}""" : "{}",
                DerivedStatus = "DELIVERED",
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            };
            orders.Add(order);
            var lines = Enumerable.Range(0, 1 + i % 3).Select(j => new OrderLine
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                OrderId = order.Id,
                ExternalLineId = $"S-L-{i}-{j}",
                Sku = i == 11 && j == 0 ? "SKU-ONLY-9" : $"S{i}-{j}",
                Barcode = j == 1 ? null : $"868{i:D4}{j}",
                TitleSnapshot = i == 5 ? "Kırmızı Kazak%_" : i is 7 or 9 or 11 ? "Düz" : titles[(i + j) % titles.Length],
                OrderedQuantity = 1,
                DeliveredQuantity = 1,
                ShippedQuantity = 1,
                UnitPrice = 50m,
                VatRate = 20m,
                RawStatus = "Delivered",
                VariantId = j == 1 && i % 5 == 0 ? lendById.Id : null
            }).ToList();
            orderLines.AddRange(lines);
            var status = (i % 4) switch { 0 => ReturnClaimStatus.Approved, 1 => ReturnClaimStatus.ActionRequired, 2 => ReturnClaimStatus.Requested, _ => ReturnClaimStatus.Rejected };
            string?[] raws = [.. lines.Select(_ => status == ReturnClaimStatus.Approved ? "Accepted" : status == ReturnClaimStatus.ActionRequired ? "WaitingInAction" : null)];
            // Claims that split: the line found by the search sits on one card only.
            if (i % 6 == 1 && lines.Count > 1) { raws[0] = "Accepted"; raws[1] = "WaitingInAction"; }
            var reason = i is 5 or 7 or 9 or 11 ? (null, null) : reasons[i % reasons.Length];
            var claim = new ReturnClaim
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                OrderId = order.Id,
                ExternalClaimId = i is 5 or 7 or 9 or 11 ? $"x{i}" : $"rc-{i}",
                Status = status,
                RawStatus = status.ToString(),
                ReasonCode = reason.Item1,
                ReasonText = reason.Item2,
                LastRemoteModifiedAt = now.AddMinutes(-i),
                RequestedAt = now.AddHours(-i),
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            };
            claims.Add(claim);
            for (var j = 0; j < lines.Count; j++)
                returnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = lines[j].Id, ExternalLineId = $"s-r-{i}-{j}", Quantity = 1m, RawStatus = raws[j] });
        }
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ProductVariants.AddRange(lendById, lendBySku);
        db.Orders.AddRange(orders);
        db.OrderLines.AddRange(orderLines);
        db.ReturnClaims.AddRange(claims);
        db.ReturnLines.AddRange(returnLines);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductVariants.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Products.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
