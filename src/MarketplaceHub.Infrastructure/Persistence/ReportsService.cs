using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace MarketplaceHub.Infrastructure.Persistence;

// The sales report. Read-only queries over the period's orders (orders with their amounts, their lines, return claims,
// packages) and the previous period of equal length, then the breakdowns in memory (ReportBuilder): one small row per
// order, line and package, so a year of a large store stays a few MB. The amounts follow the overview's revenue
// (DashboardReadService.PerformanceOrders) so both pages agree. Values inside the stored JSON snapshots (city,
// district, customer key, commission, cancel reason) are read in the database; personal data never leaves it.
public sealed class ReportsService(AppDbContext db, TimeProvider timeProvider, ReportCache? cache = null) : IReportsService
{
    private static readonly InvoiceStatus[] IssuedInvoiceStatuses =
        [InvoiceStatus.Submitted, InvoiceStatus.Accepted, InvoiceStatus.MarketplacePending, InvoiceStatus.MarketplaceFailed, InvoiceStatus.Completed];

    // How many of a report's reads run at once, each on a connection of its own (ParallelReads).
    private const int ReadWidth = 4;

    public async Task<ServiceResult<ReportSalesView>> SalesAsync(Guid tenantId, ReportSalesQuery query, CancellationToken cancellationToken)
    {
        var timezoneId = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Timezone).SingleOrDefaultAsync(cancellationToken);
        var timezone = DashboardReadService.ResolveTimezone(timezoneId);
        var now = timeProvider.GetUtcNow();
        var resolved = DashboardPerformancePeriod.Resolve(query.From, query.To, now, timezone);
        if (!resolved.Succeeded) return ServiceResult<ReportSalesView>.Fail(resolved.Error!.Code, resolved.Error.Message, resolved.Error.Status);
        var period = resolved.Value!;
        var granularity = ReportBuckets.Resolve(query.Granularity, period.From, period.To);
        if (granularity is null) return ServiceResult<ReportSalesView>.Fail("REPORT_GRANULARITY_INVALID", "Gruplama gün, hafta veya ay olmalıdır.", 400);

        var platformFilter = query.PlatformCodes?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var connectionFilter = query.ConnectionIds?.ToHashSet();
        var key = ReportCache.Key(tenantId, timezone.Id, period.From, period.To, granularity, query.IncludeTest, platformFilter, connectionFilter);
        if (cache is not null && await cache.GetAsync(db, tenantId, key, cancellationToken) is { } cached) return ServiceResult<ReportSalesView>.Ok(cached);

        var view = await SalesViewAsync(tenantId, timezone, period, granularity, query.IncludeTest, platformFilter, connectionFilter, cancellationToken);
        cache?.Set(key, new ReportCache.Entry(view, now, period.PreviousStart.ToUniversalTime(), period.CurrentEnd.ToUniversalTime(), !period.PartialDay));
        return ServiceResult<ReportSalesView>.Ok(view);
    }

    // How many whole months Stok's Tahmini Kâr averages.
    public const int ProfitRateMonths = 3;

    // Stok's Tahmini Kâr: each of the last whole months' sales report (test stores out, the asked platforms), cached as
    // any report is (ReportCache: a closed month stays until its data changes), reduced to its real profit on cost.
    public async Task<ServiceResult<ReportProfitRateView>> ProfitRateAsync(Guid tenantId, IReadOnlyList<string>? platformCodes, CancellationToken cancellationToken)
    {
        var timezoneId = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Timezone).SingleOrDefaultAsync(cancellationToken);
        var timezone = DashboardReadService.ResolveTimezone(timezoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timezone).Date);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var months = new List<ReportMonthProfitRateView>();
        for (var index = ProfitRateMonths; index > 0; index--)
        {
            var from = thisMonth.AddMonths(-index);
            var to = from.AddMonths(1).AddDays(-1);
            var report = await SalesAsync(tenantId, new ReportSalesQuery(from, to, platformCodes, null, null, false), cancellationToken);
            if (!report.Succeeded) return ServiceResult<ReportProfitRateView>.Fail(report.Error!.Code, report.Error.Message, report.Error.Status);
            months.Add(new(from, to, ReportProfitRate.Of(report.Value!)));
        }
        return ServiceResult<ReportProfitRateView>.Ok(new(months));
    }

    // The report itself. The period's and the previous period's reads do not wait for one another: they run ReadWidth at a
    // time, each on a connection of its own (ParallelReads), and each reads what it read when they ran one after another.
    private async Task<ReportSalesView> SalesViewAsync(Guid tenantId, TimeZoneInfo timezone, DashboardPerformancePeriod period, string granularity, bool includeTest,
        HashSet<string>? platformFilter, HashSet<Guid>? connectionFilter, CancellationToken cancellationToken)
    {
        var excluded = DashboardRevenuePolicy.ExcludedConnectionStatuses;
        var channels = (await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !excluded.Contains(x.Status))
                .OrderBy(x => x.PlatformCode).ThenBy(x => x.DisplayName)
                .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.Environment })
                .ToListAsync(cancellationToken))
            .Select(x => new ReportChannelView(x.Id, x.PlatformCode.Trim().ToUpperInvariant(), x.DisplayName, ReportBuckets.IsTestEnvironment(x.Environment)))
            .ToList();
        var allowed = channels
            .Where(x => includeTest || !x.IsTest)
            .Where(x => platformFilter is not { Count: > 0 } || platformFilter.Contains(x.PlatformCode))
            .Where(x => connectionFilter is not { Count: > 0 } || connectionFilter.Contains(x.Id))
            .ToDictionary(x => x.Id);
        var allowedIds = allowed.Keys.ToArray();

        // The previous period: the same number of days right before (the overview's comparison period).
        var previous = new ReportPeriod(period.PreviousFrom, period.PreviousTo, period.PreviousStart, period.PreviousEnd);
        var current = new ReportPeriod(period.From, period.To, period.CurrentStart, period.CurrentEnd);

        // The slowest reads start first; the others fill in as they finish.
        using var reads = new ParallelReads(this, cancellationToken);
        var orders = reads.Run(service => service.OrderRowsAsync(tenantId, allowedIds, current, cancellationToken));
        var previousOrders = reads.Run(service => service.OrderRowsAsync(tenantId, allowedIds, previous, cancellationToken));
        var lines = reads.Run(service => service.LineRowsAsync(tenantId, allowedIds, current, cancellationToken));
        var previousLines = reads.Run(service => service.LineRowsAsync(tenantId, allowedIds, previous, cancellationToken));
        var orderExtras = reads.Run(service => service.OrderExtrasAsync(tenantId, allowedIds, current, cancellationToken));
        var lineExtras = reads.Run(service => service.LineExtrasAsync(tenantId, allowedIds, current, cancellationToken));
        var claims = reads.Run(service => service.ClaimRowsAsync(tenantId, allowedIds, current, cancellationToken));
        var packages = reads.Run(service => service.PackageRowsAsync(tenantId, allowedIds, current, cancellationToken));
        // The previous period's addresses, commission and statement too, so its provinces, countries and profit are worked
        // out the way the period's are (the map's and the strips' change against the previous period).
        var previousOrderExtras = reads.Run(service => service.OrderExtrasAsync(tenantId, allowedIds, previous, cancellationToken));
        var previousLineExtras = reads.Run(service => service.LineExtrasAsync(tenantId, allowedIds, previous, cancellationToken));
        var previousClaims = reads.Run(service => service.ClaimRowsAsync(tenantId, allowedIds, previous, cancellationToken));
        // Only the carriers of its packages: its platform cargo is costed at the carrier's tariff, as the period's is.
        var previousCarriers = reads.Run(service => service.PackageCarriersAsync(tenantId, allowedIds, previous, cancellationToken));
        var learnedDesi = reads.Run(service => service.LearnedDesiAsync(tenantId, cancellationToken));
        var questionTimes = reads.Run(service => service.QuestionTimesAsync(tenantId, allowed, current, cancellationToken));
        var names = reads.After(lines, (service, rows) => service.CatalogNamesAsync(tenantId, rows, cancellationToken));
        var finance = reads.After(orders, (service, rows) => service.FinanceAsync(tenantId, allowed, rows, current, cancellationToken));
        var commissions = reads.After(orders, lines, (service, rows, lineRows) => service.MarketplaceCommissionsAsync(tenantId, allowed, rows, lineRows, cancellationToken));
        var declared = reads.After(orders, (service, rows) => service.DeclaredDesiAsync(tenantId, allowed, rows, cancellationToken));
        var previousFinance = reads.After(previousOrders, (service, rows) => service.FinanceAsync(tenantId, allowed, rows, previous, cancellationToken));
        var previousCommissions = reads.After(previousOrders, previousLines, (service, rows, lineRows) => service.MarketplaceCommissionsAsync(tenantId, allowed, rows, lineRows, cancellationToken));
        var previousDeclared = reads.After(previousOrders, (service, rows) => service.DeclaredDesiAsync(tenantId, allowed, rows, cancellationToken));
        await reads.AllAsync();

        var input = new ReportInput(current, granularity, timezone, allowed, orders.Result, orderExtras.Result, lines.Result, lineExtras.Result, claims.Result, packages.Result,
            names.Result, includeTest, finance.Result, commissions.Result, new ReportDesiInput(learnedDesi.Result, declared.Result));
        var previousInput = new ReportInput(previous, granularity, timezone, allowed, previousOrders.Result, previousOrderExtras.Result, previousLines.Result,
            previousLineExtras.Result, previousClaims.Result, previousCarriers.Result, names.Result, includeTest, previousFinance.Result, previousCommissions.Result,
            new ReportDesiInput(learnedDesi.Result, previousDeclared.Result));
        var stockVariants = ReportBuilder.StockCandidates(input);
        var stock = stockVariants.Length == 0
            ? new Dictionary<Guid, decimal>()
            : await db.InventoryItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && stockVariants.Contains(x.VariantId))
                .GroupBy(x => x.VariantId)
                .Select(x => new { x.Key, Available = x.Sum(item => item.Available) })
                .ToDictionaryAsync(x => x.Key, x => x.Available, cancellationToken);

        var report = ReportBuilder.Build(input, previousInput, stock) with
        {
            GeneratedAt = timeProvider.GetUtcNow(),
            Channels = channels,
            QuestionTimes = questionTimes.Result
        };
        return await WithProductDetailsAsync(tenantId, report, cancellationToken);
    }

    private TimeProvider Clock => timeProvider;
    private AppDbContext ReadContext() => new((DbContextOptions<AppDbContext>)db.GetService<IDbContextOptions>(), timeProvider);

    // Reads side by side, at most ReadWidth at once, each on a context of its own made with this one's options (a context
    // runs one query at a time). AllAsync waits for every read, also after one failed, so no context outlives the report.
    private sealed class ParallelReads(ReportsService owner, CancellationToken cancellationToken) : IDisposable
    {
        private readonly SemaphoreSlim gate = new(ReadWidth);
        private readonly List<Task> started = [];

        public Task<T> Run<T>(Func<ReportsService, Task<T>> read) => Track(RunAsync(read));

        public Task<T> After<TIn, T>(Task<TIn> first, Func<ReportsService, TIn, Task<T>> read) => Track(ThenAsync(first, read));

        public Task<T> After<TIn1, TIn2, T>(Task<TIn1> first, Task<TIn2> second, Func<ReportsService, TIn1, TIn2, Task<T>> read) =>
            Track(ThenAsync(first, second, read));

        public Task AllAsync() => Task.WhenAll(started);

        public void Dispose() => gate.Dispose();

        private Task<T> Track<T>(Task<T> task)
        {
            started.Add(task);
            return task;
        }

        private async Task<T> ThenAsync<TIn, T>(Task<TIn> first, Func<ReportsService, TIn, Task<T>> read)
        {
            var value = await first;
            return await RunAsync(service => read(service, value));
        }

        private async Task<T> ThenAsync<TIn1, TIn2, T>(Task<TIn1> first, Task<TIn2> second, Func<ReportsService, TIn1, TIn2, Task<T>> read)
        {
            var one = await first;
            var two = await second;
            return await RunAsync(service => read(service, one, two));
        }

        private async Task<T> RunAsync<T>(Func<ReportsService, Task<T>> read)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await using var context = owner.ReadContext();
                return await read(new ReportsService(context, owner.Clock));
            }
            finally
            {
                gate.Release();
            }
        }
    }

    // The answer times of the questions asked in the period on Trendyol and Hepsiburada (ReportQuestionTimeView). The
    // conversations of the stores' questions created from a month before the period are read (the table is small) and
    // the asking time is taken from the conversation itself.
    private static readonly string[] AnswerTimedPlatforms = ["TRENDYOL", "HEPSIBURADA"];

    private async Task<ReportQuestionTimeView?> QuestionTimesAsync(Guid tenantId, IReadOnlyDictionary<Guid, ReportChannelView> allowed, ReportPeriod period, CancellationToken cancellationToken)
    {
        var stores = allowed.Values.Where(x => AnswerTimedPlatforms.Contains(x.PlatformCode)).Select(x => x.Id).ToArray();
        if (stores.Length == 0) return null;
        var since = period.Start.AddDays(-31);
        var histories = await db.MarketplaceQuestions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && stores.Contains(x.ConnectionId) && x.HistoryJson != null && (x.CreatedAt == null || x.CreatedAt >= since))
            .Select(x => x.HistoryJson!).ToListAsync(cancellationToken);
        return ReportQuestionTimes.Build(histories, period.Start, period.End, allowed.Values.Where(x => stores.Contains(x.Id)).Select(x => x.PlatformCode).Distinct().Order(StringComparer.Ordinal).ToList());
    }

    // Sipariş Kârlılık Analizi: the period's orders (test stores out) with each one's profit, worked out from the same input
    // the sales report reads (ReportOrderProfit), filtered, ordered and paged here; the page's lines get their pictures.
    public async Task<ServiceResult<ReportOrderProfitPage>> OrderProfitabilityAsync(Guid tenantId, ReportOrderProfitQuery query, CancellationToken cancellationToken)
    {
        var timezoneId = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Timezone).SingleOrDefaultAsync(cancellationToken);
        var timezone = DashboardReadService.ResolveTimezone(timezoneId);
        var resolved = DashboardPerformancePeriod.Resolve(query.From, query.To, timeProvider.GetUtcNow(), timezone);
        if (!resolved.Succeeded) return ServiceResult<ReportOrderProfitPage>.Fail(resolved.Error!.Code, resolved.Error.Message, resolved.Error.Status);
        var period = resolved.Value!;
        var current = new ReportPeriod(period.From, period.To, period.CurrentStart, period.CurrentEnd);
        var excluded = DashboardRevenuePolicy.ExcludedConnectionStatuses;
        var platformFilter = query.PlatformCodes?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var allowed = (await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !excluded.Contains(x.Status))
                .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.Environment })
                .ToListAsync(cancellationToken))
            .Select(x => new ReportChannelView(x.Id, x.PlatformCode.Trim().ToUpperInvariant(), x.DisplayName, ReportBuckets.IsTestEnvironment(x.Environment)))
            .Where(x => !x.IsTest && (platformFilter is not { Count: > 0 } || platformFilter.Contains(x.PlatformCode)))
            .ToDictionary(x => x.Id);
        var input = await ProfitInputAsync(tenantId, allowed, current, timezone, cancellationToken);
        var all = ReportOrderProfit.Build(input, ReportBuilder.Facts(input).Orders);

        var term = query.Search?.Trim();
        if (!string.IsNullOrEmpty(term))
            all = all.Where(order => order.OrderNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
                || order.Lines.Any(line => line.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || line.Sku.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (line.Barcode?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
        // An order whose value is unknown goes last either way.
        Func<ReportOrderProfitView, decimal?> key = query.Sort switch
        {
            "amount" => order => order.Amount,
            "profit" => order => order.Profit,
            "profitOnCost" => order => order.ProfitOnCost,
            "margin" => order => order.Margin,
            _ => order => order.OrderedAt.UtcTicks
        };
        var known = all.Where(order => key(order) is not null);
        var ordered = (query.Descending ? known.OrderByDescending(key) : known.OrderBy(key)).ThenByDescending(order => order.OrderedAt)
            .Concat(all.Where(order => key(order) is null).OrderByDescending(order => order.OrderedAt)).ToList();
        var page = ordered.Skip(query.Page * query.PageSize).Take(query.PageSize).ToList();

        // The page's lines with the panel product's picture and options (size, colour).
        var variantIds = page.SelectMany(order => order.Lines).Where(line => line.VariantId != null).Select(line => line.VariantId!.Value).Distinct().ToArray();
        var productIds = page.SelectMany(order => order.Lines).Where(line => line.ProductId != null).Select(line => line.ProductId!.Value).Distinct().ToArray();
        var signatures = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.OptionSignature, cancellationToken);
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && productIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE"
                               && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder, item.Id
                           select new { item.ProductId, item.VariantId, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        string? Image(ReportOrderProfitLineView line)
        {
            var picked = (line.VariantId is { } variantId ? media.FirstOrDefault(x => x.VariantId == variantId) : null)
                ?? (line.ProductId is { } productId ? media.FirstOrDefault(x => x.ProductId == productId && x.VariantId == null) ?? media.FirstOrDefault(x => x.ProductId == productId) : null);
            return picked is null ? null : CatalogMediaDisplay.Url(picked.Id, picked.Classification, picked.Url);
        }
        page = page.Select(order => order with
        {
            Lines = order.Lines.Select(line => line with
            {
                ImageUrl = Image(line),
                Options = line.VariantId is { } id && signatures.TryGetValue(id, out var signature) ? signature : null
            }).ToList()
        }).ToList();
        var totals = ReportOrderProfit.Totals(all);
        return ServiceResult<ReportOrderProfitPage>.Ok(new(page, ordered.Count, query.Page, query.PageSize, totals.Amount, totals.Profit, totals.MissingCost,
            totals.ProfitAmount, totals.ProfitOrders, totals.Margin, totals.MissingCargo, totals.MissingCommission));
    }

    // The period's orders of the allowed stores with everything the order profit costs them from (ReportOrderProfit).
    private async Task<ReportInput> ProfitInputAsync(Guid tenantId, Dictionary<Guid, ReportChannelView> allowed, ReportPeriod current, TimeZoneInfo timezone,
        CancellationToken cancellationToken)
    {
        var allowedIds = allowed.Keys.ToArray();
        var orders = await OrderRowsAsync(tenantId, allowedIds, current, cancellationToken);
        var lines = await LineRowsAsync(tenantId, allowedIds, current, cancellationToken);
        return new ReportInput(current, ReportBuckets.Day, timezone, allowed, orders, await OrderExtrasAsync(tenantId, allowedIds, current, cancellationToken), lines,
            await LineExtrasAsync(tenantId, allowedIds, current, cancellationToken), await ClaimRowsAsync(tenantId, allowedIds, current, cancellationToken),
            await PackageRowsAsync(tenantId, allowedIds, current, cancellationToken), await CatalogNamesAsync(tenantId, lines, cancellationToken), false,
            await FinanceAsync(tenantId, allowed, orders, current, cancellationToken),
            await MarketplaceCommissionsAsync(tenantId, allowed, orders, lines, cancellationToken),
            new ReportDesiInput(await LearnedDesiAsync(tenantId, cancellationToken), await DeclaredDesiAsync(tenantId, allowed, orders, cancellationToken)));
    }

    // Fiyat Hesapla's hints: each live store's commission, cargo and service fee on the clothing orders of the last 60
    // days up to today (ReportPriceAssumptions), costed as Sipariş Kârlılık Analizi costs them.
    public async Task<ServiceResult<PriceAssumptionsView>> PriceAssumptionsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var timezoneId = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Timezone).SingleOrDefaultAsync(cancellationToken);
        var timezone = DashboardReadService.ResolveTimezone(timezoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timezone).DateTime);
        var resolved = DashboardPerformancePeriod.Resolve(today.AddDays(1 - ReportPriceAssumptions.Days), today, timeProvider.GetUtcNow(), timezone);
        if (!resolved.Succeeded) return ServiceResult<PriceAssumptionsView>.Fail(resolved.Error!.Code, resolved.Error.Message, resolved.Error.Status);
        var period = resolved.Value!;
        var current = new ReportPeriod(period.From, period.To, period.CurrentStart, period.CurrentEnd);
        var excluded = DashboardRevenuePolicy.ExcludedConnectionStatuses;
        var allowed = (await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !excluded.Contains(x.Status))
                .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.Environment })
                .ToListAsync(cancellationToken))
            .Select(x => new ReportChannelView(x.Id, x.PlatformCode.Trim().ToUpperInvariant(), x.DisplayName, ReportBuckets.IsTestEnvironment(x.Environment)))
            .Where(x => !x.IsTest)
            .ToDictionary(x => x.Id);
        var input = await ProfitInputAsync(tenantId, allowed, current, timezone, cancellationToken);

        // The marketplace category each panel category is mapped to, by its path in that marketplace's tree.
        var mapped = await (from mapping in db.CategoryMappings.AsNoTracking()
                            join item in db.ReferenceItems.AsNoTracking()
                                on new { mapping.TenantId, mapping.ConnectionId, mapping.ExternalId, ResourceType = "CATEGORIES" }
                                equals new { item.TenantId, item.ConnectionId, item.ExternalId, item.ResourceType }
                            where mapping.TenantId == tenantId
                            select new { mapping.LocalId, item.Path })
            .Distinct()
            .ToListAsync(cancellationToken);
        var clothing = ReportPriceAssumptions.ClothingCategories(mapped.Select(x => (x.LocalId, (string?)x.Path)));

        // Hepsiburada's service charges on the period's orders, from its accounting records.
        var hepsiburada = allowed.Values.Where(channel => channel.PlatformCode == "HEPSIBURADA").Select(channel => channel.Id).ToArray();
        var numbers = input.Orders.Where(order => hepsiburada.Contains(order.ConnectionId)).Select(order => order.OrderNumber).Distinct(StringComparer.Ordinal).ToArray();
        var feeTypes = ReportPriceAssumptions.HepsiburadaServiceFeeTypes.ToArray();
        var fees = numbers.Length == 0 ? [] : (await db.MarketplaceFinanceTransactions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && hepsiburada.Contains(x.ConnectionId) && x.Source == Jobs.Handlers.HepsiburadaFinanceSyncJobHandler.Source
                    && x.OrderNumber != null && numbers.Contains(x.OrderNumber) && x.TransactionType != null && feeTypes.Contains(x.TransactionType))
                .Select(x => new { x.ExternalId, x.ConnectionId, x.OrderNumber, x.Debt, x.Credit })
                .ToListAsync(cancellationToken))
            .DistinctBy(x => (x.ConnectionId, x.ExternalId))
            .Select(x => new ReportPriceAssumptions.FeeRecord(x.ConnectionId, x.OrderNumber!, x.Debt - x.Credit))
            .ToList();
        return ServiceResult<PriceAssumptionsView>.Ok(ReportPriceAssumptions.Build(input, ReportBuilder.Facts(input).Orders, clothing, fees));
    }

    // The listed products' catalogue details, as the product list shows them: the variant's model code and options
    // (size, colour…) and a picture, the variant's own first, else the product's. Only the top lists are looked up.
    private async Task<ReportSalesView> WithProductDetailsAsync(Guid tenantId, ReportSalesView report, CancellationToken cancellationToken)
    {
        var listed = report.TopProducts.Concat(report.TopReturned).Concat(report.Rising ?? []).Concat(report.Falling ?? []).ToList();
        var variantIds = listed.Where(x => x.VariantId != null).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var productIds = listed.Where(x => x.ProductId != null).Select(x => x.ProductId!.Value).Distinct().ToArray();
        if (variantIds.Length == 0 && productIds.Length == 0) return report;
        var modelCodes = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && variantIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.ModelCode, cancellationToken);
        var optionRows = await (from assignment in db.VariantOptionValues.AsNoTracking()
                                join option in db.ProductOptions.AsNoTracking() on new { assignment.TenantId, assignment.OptionId } equals new { option.TenantId, OptionId = option.Id }
                                join value in db.ProductOptionValues.AsNoTracking() on new { assignment.TenantId, assignment.OptionValueId } equals new { value.TenantId, OptionValueId = value.Id }
                                where assignment.TenantId == tenantId && variantIds.Contains(assignment.VariantId)
                                orderby option.SortOrder
                                select new { assignment.VariantId, Name = option.Label, Value = value.Label }).ToListAsync(cancellationToken);
        var options = optionRows.GroupBy(x => x.VariantId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ReportProductOptionView>)group.Select(x => new ReportProductOptionView(x.Name, x.Value)).ToList());
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && productIds.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE"
                               && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder, item.Id
                           select new { item.ProductId, item.VariantId, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        string? Image(ReportProductView product)
        {
            var picked = (product.VariantId is { } variantId ? media.FirstOrDefault(x => x.VariantId == variantId) : null)
                ?? (product.ProductId is { } productId ? media.FirstOrDefault(x => x.ProductId == productId && x.VariantId == null) ?? media.FirstOrDefault(x => x.ProductId == productId) : null);
            return picked is null ? null : CatalogMediaDisplay.Url(picked.Id, picked.Classification, picked.Url);
        }
        ReportProductView Detailed(ReportProductView product) => product with
        {
            ModelCode = product.VariantId is { } variantId && modelCodes.TryGetValue(variantId, out var code) && !string.IsNullOrWhiteSpace(code) ? code : null,
            ImageUrl = Image(product),
            Options = product.VariantId is { } id && options.TryGetValue(id, out var list) ? list : []
        };
        return report with
        {
            TopProducts = report.TopProducts.Select(Detailed).ToList(),
            TopReturned = report.TopReturned.Select(Detailed).ToList(),
            Rising = report.Rising?.Select(Detailed).ToList(),
            Falling = report.Falling?.Select(Detailed).ToList()
        };
    }

    // The period's orders by their order time (a real instant on every platform), any status.
    private IQueryable<PeriodOrder> PeriodOrders(Guid tenantId, Guid[] allowedIds, ReportPeriod period)
    {
        var start = period.Start;
        var end = period.End;
        return from order in db.Orders.AsNoTracking()
               where order.TenantId == tenantId && allowedIds.Contains(order.ConnectionId)
                   && order.OrderedAt >= start && order.OrderedAt < end
               select new PeriodOrder { Order = order, RealAt = order.OrderedAt };
    }

    // One row per order. Amount: the order's current packages that are not cancelled (an order whose packages carry
    // no amount keeps its order total) plus the marketplace's own discount on the lines that are not cancelled
    // (DashboardRevenuePolicy.Amount); the returned part is worked out from these in ReportBuilder, as the overview does.
    private async Task<List<ReportOrderRow>> OrderRowsAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken)
    {
        var acceptedClaims = DashboardRevenuePolicy.AcceptedReturnStatuses;
        var acceptedLines = DashboardRevenuePolicy.AcceptedReturnLineRawStatuses;
        var currentPackages = MarketplaceSalesService.CurrentShipmentPackages(db).Where(package => package.TenantId == tenantId);
        var orderLines = db.OrderLines.AsNoTracking().Where(line => line.TenantId == tenantId);
        var allocations = db.PackageLineAllocations.AsNoTracking().Where(allocation => allocation.TenantId == tenantId);
        var unbookedReturns = DashboardRevenuePolicy.OrdersWithUnbookedReturn(db, tenantId);
        return await (from row in PeriodOrders(tenantId, allowedIds, period)
                      let order = row.Order
                      let priced = currentPackages.Any(package => package.OrderId == order.Id && (package.NetAmount > 0 || package.GrossAmount > 0))
                      let paid = priced
                          ? currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                              .Sum(package => package.NetAmount > 0 ? package.NetAmount : package.GrossAmount)
                          : (order.NetAmount > 0 ? order.NetAmount : order.GrossAmount)
                      let listAmount = priced
                          ? currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                              .Sum(package => package.GrossAmount > 0 ? package.GrossAmount : package.NetAmount)
                          : (order.GrossAmount > 0 ? order.GrossAmount : order.NetAmount)
                      let platformDiscount = orderLines.Where(line => line.OrderId == order.Id && line.PlatformDiscount != null)
                          .Sum(line => line.PlatformDiscount!.Value * (line.OrderedQuantity - line.CancelledQuantity))
                      // The lines at the same base as the amount: the price paid plus the marketplace's own discount.
                      let lineValue = orderLines.Where(line => line.OrderId == order.Id)
                          .Sum(line => (line.UnitPrice + (line.PlatformDiscount ?? 0m)) * (line.OrderedQuantity - line.CancelledQuantity))
                      // Never above the list price: an amount that already is the list price (no net known) takes nothing more.
                      // Hepsiburada's list is the price paid, so its own discount (HepsiburadaJsonMapper.PlatformDiscount) is
                      // bounded by the lines' value at that base instead (DashboardReadService.PerformanceOrders, the same).
                      let ceiling = listAmount > paid ? (listAmount > lineValue ? listAmount : lineValue) : (paid > lineValue ? paid : lineValue)
                      let amount = paid + platformDiscount > ceiling ? ceiling : paid + platformDiscount
                      // A Trendyol micro-export order's return (claim, returned line or package, returned status) counts once Trendyol's
                      // statement records the refund, on the overview too (DashboardRevenuePolicy.OrdersWithUnbookedReturn).
                      let returnBooked = !unbookedReturns.Contains(order.Id)
                      let packagesBack = priced && returnBooked
                          ? currentPackages.Where(package => package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Returned)
                              .Sum(package => package.NetAmount > 0 ? package.NetAmount : package.GrossAmount)
                            + (from package in currentPackages
                               where package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Returned
                               join allocation in allocations on package.Id equals allocation.PackageId
                               join line in orderLines on allocation.OrderLineId equals line.Id
                               where line.PlatformDiscount != null
                               select line.PlatformDiscount!.Value * allocation.AllocatedQuantity).Sum()
                          : 0m
                      let lineBack = orderLines.Where(line => line.OrderId == order.Id)
                          .Select(line => new
                          {
                              UnitPrice = line.UnitPrice + (line.PlatformDiscount ?? 0m),
                              Active = line.OrderedQuantity - line.CancelledQuantity,
                              ReturnedQuantity = returnBooked ? line.ReturnedQuantity : 0m,
                              Accepted = !returnBooked ? 0m : db.ReturnLines.Where(returned => returned.TenantId == tenantId && returned.OrderLineId == line.Id
                                      && db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.Id == returned.ClaimId && (claim.CustomerRequest != ReturnCustomerRequestPolicy.Change || claim.RawStatus.ToUpper() == ReturnCustomerRequestPolicy.RefundedRawStatus)
                                          && (returned.RawStatus == null || returned.RawStatus == ""
                                              ? acceptedClaims.Contains(claim.Status)
                                              : acceptedLines.Contains(returned.RawStatus.ToUpper()))))
                                  .Sum(returned => (decimal?)returned.Quantity) ?? 0m
                          })
                          .Sum(line => line.UnitPrice * Math.Min(line.Active, Math.Max(line.ReturnedQuantity, line.Accepted)))
                      select new ReportOrderRow
                      {
                          Id = order.Id,
                          ConnectionId = order.ConnectionId,
                          OrderNumber = order.OrderNumber,
                          Currency = order.Currency,
                          DerivedStatus = order.DerivedStatus,
                          Amount = amount,
                          ListAmount = listAmount,
                          OrderListAmount = order.GrossAmount,
                          PackagesBack = packagesBack,
                          LineValue = lineValue,
                          LineBack = lineBack,
                          RealAt = row.RealAt,
                          OrderedAt = order.OrderedAt,
                          ShipmentDueAt = order.ShipmentDueAt,
                          StoreFrontCode = order.StoreFrontCode,
                          ReturnBooked = returnBooked,
                          UnreadableLines = currentPackages.Where(package => package.OrderId == order.Id && package.Status != ShipmentPackageStatus.Cancelled)
                              .Sum(package => package.UnreadableLineCount)
                      }).ToListAsync(cancellationToken);
    }

    // One row per order line, with the units accepted back by return claims, the cost price and desi the line kept
    // from its variant at the sale (OrderLineCostSnapshot; the variant's current ones when it kept none) and
    // its product's brand and category. A Trendyol micro-export order's accepted claim counts only once Trendyol's
    // statement records the return: 23-30 Sep 2026, a Saudi order's three accepted units had no
    // refund and Trendyol's sales report did not count them (17 units back, the panel 20).
    private async Task<List<ReportLineRow>> LineRowsAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken)
    {
        var acceptedClaims = DashboardRevenuePolicy.AcceptedReturnStatuses;
        var acceptedLines = DashboardRevenuePolicy.AcceptedReturnLineRawStatuses;
        var unbookedReturns = DashboardRevenuePolicy.OrdersWithUnbookedReturn(db, tenantId);
        return await (from row in PeriodOrders(tenantId, allowedIds, period)
                      join line in db.OrderLines.AsNoTracking() on new { row.Order.TenantId, OrderId = row.Order.Id } equals new { line.TenantId, line.OrderId }
                      join variant in db.ProductVariants.AsNoTracking() on new { line.TenantId, Id = line.VariantId } equals new { variant.TenantId, Id = (Guid?)variant.Id } into variants
                      from variant in variants.DefaultIfEmpty()
                      join product in db.Products.AsNoTracking() on new { TenantId = (Guid?)variant.TenantId, Id = (Guid?)variant.ProductId } equals new { TenantId = (Guid?)product.TenantId, Id = (Guid?)product.Id } into products
                      from product in products.DefaultIfEmpty()
                      select new ReportLineRow
                      {
                          Id = line.Id,
                          OrderId = line.OrderId,
                          VariantId = line.VariantId,
                          ProductId = variant == null ? null : (Guid?)variant.ProductId,
                          BrandId = product == null ? null : product.BrandId,
                          CategoryId = product == null ? null : product.CategoryId,
                          Title = line.TitleSnapshot,
                          Sku = line.Sku,
                          Barcode = line.Barcode,
                          UnitPrice = line.UnitPrice,
                          PlatformDiscount = line.PlatformDiscount ?? 0m,
                          OrderedQuantity = line.OrderedQuantity,
                          CancelledQuantity = line.CancelledQuantity,
                          ReturnedQuantity = unbookedReturns.Contains(row.Order.Id) ? 0m : line.ReturnedQuantity,
                          AcceptedReturnQuantity = unbookedReturns.Contains(row.Order.Id)
                              ? 0m
                              : db.ReturnLines.Where(returned => returned.TenantId == tenantId && returned.OrderLineId == line.Id
                                  && db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.Id == returned.ClaimId && (claim.CustomerRequest != ReturnCustomerRequestPolicy.Change || claim.RawStatus.ToUpper() == ReturnCustomerRequestPolicy.RefundedRawStatus)
                                      && (returned.RawStatus == null || returned.RawStatus == ""
                                          ? acceptedClaims.Contains(claim.Status)
                                          : acceptedLines.Contains(returned.RawStatus.ToUpper()))))
                              .Sum(returned => (decimal?)returned.Quantity) ?? 0m,
                          CostPrice = line.UnitCostSnapshot ?? (variant == null ? null : variant.CostPrice),
                          VatRate = line.VatRate,
                          Desi = line.DesiSnapshot ?? (variant == null ? null : variant.Desi)
                      }).ToListAsync(cancellationToken);
    }

    private async Task<List<ReportClaimRow>> ClaimRowsAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken)
    {
        var unbookedReturns = DashboardRevenuePolicy.OrdersWithUnbookedReturn(db, tenantId);
        return await (from row in PeriodOrders(tenantId, allowedIds, period)
                      join claim in db.ReturnClaims.AsNoTracking() on new { row.Order.TenantId, OrderId = row.Order.Id } equals new { claim.TenantId, claim.OrderId }
                      // A micro-export order's claim counts once Trendyol's statement records the return, as its units do (LineRowsAsync).
                      // An exchange is no return: the sale goes on (owner, 6 Nov 2026).
                      where !unbookedReturns.Contains(row.Order.Id) && (claim.CustomerRequest != ReturnCustomerRequestPolicy.Change || claim.RawStatus.ToUpper() == ReturnCustomerRequestPolicy.RefundedRawStatus)
                      select new ReportClaimRow { OrderId = claim.OrderId, ConnectionId = claim.ConnectionId, Status = claim.Status, ReasonCode = claim.ReasonCode, ReasonText = claim.ReasonText })
            .ToListAsync(cancellationToken);
    }

    // The current packages of the period's orders with their first "shipped" and "delivered" status times and what
    // tells whether they hold an invoice.
    private async Task<List<ReportPackageRow>> PackageRowsAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken)
    {
        var issued = IssuedInvoiceStatuses;
        return await (from row in PeriodOrders(tenantId, allowedIds, period)
                      join package in MarketplaceSalesService.CurrentShipmentPackages(db) on new { row.Order.TenantId, OrderId = row.Order.Id } equals new { package.TenantId, package.OrderId }
                      select new ReportPackageRow
                      {
                          Id = package.Id,
                          OrderId = package.OrderId,
                          CargoProvider = package.CargoProviderExternalId,
                          Status = package.Status,
                          Amount = package.NetAmount > 0 ? package.NetAmount : package.GrossAmount,
                          MarketplaceInvoiceStatus = package.MarketplaceInvoiceStatus,
                          ManualInvoiceStatus = package.ManualInvoiceStatus,
                          PanelInvoiced = db.Invoices.Any(invoice => invoice.TenantId == tenantId && invoice.OrderId == package.OrderId
                              && (invoice.PackageId == null || invoice.PackageId == package.Id) && issued.Contains(invoice.Status)),
                          ShippedAt = db.OrderStatusHistory.Where(history => history.TenantId == tenantId && history.PackageId == package.Id && history.CanonicalStatus == "SHIPPED")
                              .Min(history => (DateTimeOffset?)history.OccurredAt),
                          DeliveredAt = db.OrderStatusHistory.Where(history => history.TenantId == tenantId && history.PackageId == package.Id && history.CanonicalStatus == "DELIVERED")
                              .Min(history => (DateTimeOffset?)history.OccurredAt)
                      }).ToListAsync(cancellationToken);
    }

    // The carrier of each current package of the period's orders and nothing else, in one narrow read: rows holding only
    // OrderId and CargoProvider, for the platform cargo estimate (ReportCosts), which reads no other field of the
    // previous period's packages.
    private async Task<List<ReportPackageRow>> PackageCarriersAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken) =>
        await (from row in PeriodOrders(tenantId, allowedIds, period)
               join package in MarketplaceSalesService.CurrentShipmentPackages(db) on new { row.Order.TenantId, OrderId = row.Order.Id } equals new { package.TenantId, package.OrderId }
               where package.CargoProviderExternalId != null && package.CargoProviderExternalId != ""
               select new ReportPackageRow { OrderId = package.OrderId, CargoProvider = package.CargoProviderExternalId })
            .ToListAsync(cancellationToken);

    // City, district and a one-way customer key from the order's JSON snapshots, read in the database. A snapshot that
    // is not valid JSON reads as empty. The customer key is the store's own customer id, else the e-mail, else the
    // last ten digits of the phone, each as an MD5 digest; the text itself is never returned. ClaimBuilt: the customer
    // snapshot still carries the return-claim mark (TrendyolJsonMapper.IsReturnClaimReadModelSnapshot). The orders are the
    // period's (PeriodOrders' conditions), so the read need not wait for OrderRowsAsync. The snapshots are parsed once
    // (MATERIALIZED): inlined, every a->> and c->> parsed the whole snapshot again (~1 s for a year on live, 0.19 s once).
    private async Task<Dictionary<Guid, ReportOrderExtra>> OrderExtrasAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken)
    {
        if (allowedIds.Length == 0) return [];
        const string sql = """
            WITH src AS MATERIALIZED (
                SELECT o."Id", o."ConnectionId",
                       CASE WHEN pg_input_is_valid(o."ShipmentAddressSnapshotJson", 'jsonb') THEN o."ShipmentAddressSnapshotJson"::jsonb END AS a,
                       CASE WHEN pg_input_is_valid(o."CustomerSnapshotJson", 'jsonb') THEN o."CustomerSnapshotJson"::jsonb END AS c
                FROM sales.orders o
                WHERE o."TenantId" = @tenant AND o."ConnectionId" = ANY(@connections) AND o."OrderedAt" >= @start AND o."OrderedAt" < @end),
            keys AS (
                SELECT "Id", a, c,
                       nullif(trim(coalesce(c->>'customerId', c->>'CustomerId', '')), '') AS customer_id,
                       lower(trim(coalesce(nullif(c->>'email', ''), nullif(c->>'customerEmail', ''), nullif(a->>'email', ''), nullif(a->>'customerEmail', ''), ''))) AS email,
                       regexp_replace(coalesce(nullif(c->>'phone', ''), nullif(c->>'phoneNumber', ''), nullif(c->>'gsm', ''), nullif(a->>'phone', ''), nullif(a->>'phoneNumber', ''), nullif(a->>'gsm', ''), ''), '\D', '', 'g') AS phone,
                       "ConnectionId"
                FROM src)
            SELECT "Id",
                   a->>'cityName' AS "CityName", a->>'city' AS "City", a->>'shippingCity' AS "ShippingCity", a->>'province' AS "Province",
                   a->>'districtName' AS "DistrictName", a->>'district' AS "District", a->>'shippingDistrict' AS "ShippingDistrict",
                   a->>'town' AS "Town", a->>'shippingTown' AS "ShippingTown",
                   coalesce(nullif(a->>'countryCode', ''), nullif(a->>'shippingCountryCode', '')) AS "CountryCode",
                   CASE WHEN customer_id IS NOT NULL AND customer_id <> '0' THEN 'i:' || md5("ConnectionId"::text || ':' || customer_id)
                        WHEN email LIKE '%_@_%' THEN 'e:' || md5(email)
                        WHEN length(phone) >= 10 THEN 'p:' || md5(right(phone, 10))
                   END AS "CustomerKey",
                   coalesce(c->>'_ravenciaReadModelSource' = 'RETURN_CLAIM'
                       OR (c->>'claimId' IS NOT NULL AND c->>'claimDate' IS NOT NULL
                           AND (c->>'orderOutboundPackageId' IS NOT NULL OR c->>'orderShipmentPackageId' IS NOT NULL)), false) AS "ClaimBuilt"
            FROM keys
            """;
        var rows = await db.Database.SqlQueryRaw<ReportOrderExtra>(sql, PeriodParameters(tenantId, allowedIds, period))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(x => x.Id);
    }

    // The tenant, the stores and the period's bounds for the raw reads of the period's orders (OrderExtrasAsync, LineExtrasAsync).
    private static object[] PeriodParameters(Guid tenantId, Guid[] allowedIds, ReportPeriod period) =>
    [
        new NpgsqlParameter("tenant", tenantId),
        new NpgsqlParameter("connections", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = allowedIds },
        new NpgsqlParameter("start", NpgsqlDbType.TimestampTz) { Value = period.Start.ToUniversalTime() },
        new NpgsqlParameter("end", NpgsqlDbType.TimestampTz) { Value = period.End.ToUniversalTime() }
    ];

    // The commission each marketplace sent on the line (Trendyol "commission" rate, Hepsiburada "commission" amount
    // object, n11 "commissionRate", PTT AVM "Komisyon") and a cancel reason, read in the database, from the lines of the
    // period's orders; each snapshot parsed once (MATERIALIZED, as OrderExtrasAsync).
    private async Task<Dictionary<Guid, ReportLineExtra>> LineExtrasAsync(Guid tenantId, Guid[] allowedIds, ReportPeriod period, CancellationToken cancellationToken)
    {
        if (allowedIds.Length == 0) return [];
        const string sql = """
            WITH src AS MATERIALIZED (
                SELECT l."Id",
                       CASE WHEN l."SourceSnapshotJson" IS NOT NULL AND pg_input_is_valid(l."SourceSnapshotJson", 'jsonb') THEN l."SourceSnapshotJson"::jsonb END AS j
                FROM sales.order_lines l
                JOIN sales.orders o ON o."TenantId" = l."TenantId" AND o."Id" = l."OrderId"
                WHERE l."TenantId" = @tenant AND o."ConnectionId" = ANY(@connections) AND o."OrderedAt" >= @start AND o."OrderedAt" < @end)
            SELECT "Id",
                   jsonb_typeof(j->'commission') AS "CommissionType",
                   CASE WHEN jsonb_typeof(j->'commission') = 'object' THEN j->'commission'->>'amount' ELSE j->>'commission' END AS "Commission",
                   j->>'commissionRate' AS "CommissionRate",
                   j->>'Komisyon' AS "Komisyon",
                   CASE WHEN j->>'lineGrossAmount' ~ '^[0-9]+([.][0-9]+)?$' THEN (j->>'lineGrossAmount')::numeric END AS "ListUnitPrice",
                   coalesce(nullif(j->>'cancelReason', ''), nullif(j->>'cancelReasonCode', ''), nullif(j->>'cancellationReason', ''), nullif(j->>'CancelReason', ''), nullif(j->>'IptalNedeni', '')) AS "CancelReason"
            FROM src
            """;
        var rows = await db.Database.SqlQueryRaw<ReportLineExtra>(sql, PeriodParameters(tenantId, allowedIds, period))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(x => x.Id);
    }

    // The accounting records of the report's production Trendyol stores (ReportCosts): those tied to an order of the
    // period, and those dated in the period that belong to no order the panel knows (fees, withholding tax, a record of
    // an order never imported). A record of an order of another period is left to that period. Cargo invoice lines
    // follow the same rule, by their order or by their invoice's date. Null when the report has no such store.
    // The real commission of the period's Hepsiburada, n11 and Pazarama orders from their marketplace's own finance records
    // (ReportMarketplaceCommissions): Hepsiburada's commission records by order number or package number, n11's
    // settlement lines and Pazarama's money movements by order number. An order without complete records is not here.
    private async Task<Dictionary<Guid, decimal>?> MarketplaceCommissionsAsync(Guid tenantId, IReadOnlyDictionary<Guid, ReportChannelView> allowed, IReadOnlyList<ReportOrderRow> orders,
        IReadOnlyList<ReportLineRow> lines, CancellationToken cancellationToken)
    {
        var platforms = allowed.Values.Where(channel => channel.PlatformCode is "HEPSIBURADA" or "N11" or "PAZARAMA").ToDictionary(channel => channel.Id, channel => channel.PlatformCode);
        var own = orders.Where(order => platforms.ContainsKey(order.ConnectionId)).ToList();
        if (own.Count == 0) return null;
        var storeIds = platforms.Keys.ToArray();
        var hepsiburada = own.Where(order => platforms[order.ConnectionId] == "HEPSIBURADA").Select(order => order.Id).ToArray();
        var packages = new List<(Guid OrderId, string Number)>();
        foreach (var chunk in hepsiburada.Chunk(2_000))
            packages.AddRange((await MarketplaceSalesService.CurrentShipmentPackages(db)
                    .Where(package => package.TenantId == tenantId && chunk.Contains(package.OrderId) && package.Status != ShipmentPackageStatus.Cancelled)
                    .Select(package => new { package.OrderId, package.ExternalPackageId })
                    .ToListAsync(cancellationToken))
                .Select(package => (package.OrderId, package.ExternalPackageId)));
        var packagesByOrder = packages.GroupBy(package => package.OrderId).ToDictionary(group => group.Key, group => group.Select(package => package.Number).Distinct(StringComparer.Ordinal).ToList());
        var packageNumbers = packages.Select(package => package.Number).Distinct(StringComparer.Ordinal).ToArray();
        var numbers = own.Select(order => order.OrderNumber).Distinct(StringComparer.Ordinal).ToArray();
        var sources = new[] { Jobs.Handlers.HepsiburadaFinanceSyncJobHandler.Source, Jobs.Handlers.N11SettlementSyncJobHandler.Source, Jobs.Handlers.PazaramaFinanceSyncJobHandler.Source };
        var records = await db.MarketplaceFinanceTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && storeIds.Contains(x.ConnectionId) && sources.Contains(x.Source)
                && ((x.OrderNumber != null && numbers.Contains(x.OrderNumber)) || (x.OrderNumber == null && x.ShipmentPackageId != null && packageNumbers.Contains(x.ShipmentPackageId))))
            .Select(x => new ReportMarketplaceCommissions.Record(x.ConnectionId, x.Source, x.ExternalId, x.TransactionType, x.Category, x.OrderNumber, x.ShipmentPackageId,
                x.Debt, x.Credit, x.CommissionAmount, x.CommissionRate))
            .ToListAsync(cancellationToken);
        var activeLines = lines.Where(line => line.OrderedQuantity - line.CancelledQuantity > 0).GroupBy(line => line.OrderId).ToDictionary(group => group.Key, group => group.Count());
        return ReportMarketplaceCommissions.ByOrder(
            own.Select(order => new ReportMarketplaceCommissions.Order(order.Id, order.ConnectionId, platforms[order.ConnectionId], order.OrderNumber,
                activeLines.GetValueOrDefault(order.Id), packagesByOrder.GetValueOrDefault(order.Id) ?? [])).ToList(),
            records);
    }

    // The unit desi learned from every billed Trendyol shipping parcel of the tenant's last year, whatever stores the
    // report shows: the same products go to every marketplace in the same parcels. A parcel is one order with one
    // shipping line; an order billed in several parcels says nothing of one parcel's contents and is left out.
    private async Task<ReportParcelDesi.Learned> LearnedDesiAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow().ToUniversalTime().AddDays(-365);
        var items = await db.MarketplaceCargoInvoiceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderNumber != null && x.Desi != null && x.FetchedAt >= since)
            .Select(x => new { x.ConnectionId, OrderNumber = x.OrderNumber!, x.ShipmentPackageType, x.ParcelUniqueId, Desi = x.Desi!.Value })
            .ToListAsync(cancellationToken);
        var parcels = items
            .Where(x => !TrendyolFinanceCategorizer.Fold(x.ShipmentPackageType).Contains("iade", StringComparison.Ordinal))
            .GroupBy(x => (x.ConnectionId, x.OrderNumber))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => (decimal)group.First().Desi);
        if (parcels.Count == 0) return ReportParcelDesi.Learned.None;

        var lines = new List<(Guid ConnectionId, string OrderNumber, ReportParcelDesi.ParcelLine Line)>();
        foreach (var chunk in parcels.Keys.Select(key => key.OrderNumber).Distinct(StringComparer.Ordinal).Chunk(2_000))
            lines.AddRange((await (from order in db.Orders.AsNoTracking()
                                   where order.TenantId == tenantId && chunk.Contains(order.OrderNumber)
                                   join line in db.OrderLines.AsNoTracking() on new { order.TenantId, OrderId = order.Id } equals new { line.TenantId, line.OrderId }
                                   join variant in db.ProductVariants.AsNoTracking() on new { line.TenantId, Id = line.VariantId ?? Guid.Empty } equals new { variant.TenantId, variant.Id } into variants
                                   from variant in variants.DefaultIfEmpty()
                                   join product in db.Products.AsNoTracking() on new { TenantId = (Guid?)variant.TenantId, Id = (Guid?)variant.ProductId } equals new { TenantId = (Guid?)product.TenantId, Id = (Guid?)product.Id } into products
                                   from product in products.DefaultIfEmpty()
                                   select new
                                   {
                                       order.ConnectionId,
                                       order.OrderNumber,
                                       ProductId = variant == null ? null : (Guid?)variant.ProductId,
                                       CategoryId = product == null ? null : product.CategoryId,
                                       Units = line.OrderedQuantity - line.CancelledQuantity
                                   }).ToListAsync(cancellationToken))
                .Select(row => (row.ConnectionId, row.OrderNumber, new ReportParcelDesi.ParcelLine(row.ProductId, row.CategoryId, row.Units))));
        return ReportParcelDesi.Learn(lines
            .Where(row => parcels.ContainsKey((row.ConnectionId, row.OrderNumber)))
            .GroupBy(row => (row.ConnectionId, row.OrderNumber))
            .Select(group => new ReportParcelDesi.Parcel(parcels[group.Key], group.Select(row => row.Line).ToList())));
    }

    // By Hepsiburada order: the desi Hepsiburada put on its lines, added up; an order with a line without one has none.
    private async Task<IReadOnlyDictionary<Guid, decimal>> DeclaredDesiAsync(Guid tenantId, IReadOnlyDictionary<Guid, ReportChannelView> allowed, IReadOnlyList<ReportOrderRow> orders,
        CancellationToken cancellationToken)
    {
        var stores = allowed.Values.Where(channel => channel.PlatformCode == "HEPSIBURADA").Select(channel => channel.Id).ToHashSet();
        var orderIds = orders.Where(order => stores.Contains(order.ConnectionId)).Select(order => order.Id).ToArray();
        var result = new Dictionary<Guid, decimal>();
        foreach (var chunk in orderIds.Chunk(2_000))
        {
            var lines = await db.OrderLines.AsNoTracking()
                .Where(line => line.TenantId == tenantId && chunk.Contains(line.OrderId))
                .Select(line => new { line.OrderId, line.SourceSnapshotJson })
                .ToListAsync(cancellationToken);
            foreach (var order in lines.GroupBy(line => line.OrderId))
            {
                var desi = order.Select(line => ReportParcelDesi.HepsiburadaLineDesi(line.SourceSnapshotJson)).ToList();
                if (desi.All(value => value is not null)) result[order.Key] = desi.Sum(value => value!.Value);
            }
        }
        return result;
    }

    private async Task<ReportFinance?> FinanceAsync(Guid tenantId, IReadOnlyDictionary<Guid, ReportChannelView> allowed, IReadOnlyList<ReportOrderRow> orders,
        ReportPeriod period, CancellationToken cancellationToken)
    {
        var storeIds = allowed.Values.Where(x => x.PlatformCode == "TRENDYOL" && !x.IsTest).Select(x => x.Id).ToArray();
        if (storeIds.Length == 0) return null;
        var stored = await db.MarketplaceFinanceSyncStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && storeIds.Contains(x.ConnectionId))
            .ToDictionaryAsync(x => x.ConnectionId, cancellationToken);
        var states = storeIds.ToDictionary(id => id, id => stored.GetValueOrDefault(id));
        if (stored.Count == 0) return new([], [], new HashSet<(Guid, string)>(), states);

        var start = period.Start;
        var end = period.End;
        var rows = new List<ReportFinanceRow>();
        var items = new List<ReportCargoItemRow>();
        var itemized = new HashSet<(Guid ConnectionId, string Serial)>();
        var samples = new Dictionary<Guid, ReportCargoSample>();
        // Store by store: an order number is only that store's order.
        foreach (var storeId in stored.Keys)
        {
            var numbers = orders.Where(x => x.ConnectionId == storeId && !string.IsNullOrEmpty(x.OrderNumber)).Select(x => x.OrderNumber).Distinct(StringComparer.Ordinal).ToArray();
            var storeRows = await db.MarketplaceFinanceTransactions.AsNoTracking()
                // Payouts and transfers move money, they are no cost (MarketplaceFinanceSources.MoneyMovements).
                .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId && x.Source != MarketplaceFinanceSources.MoneyMovements
                    && ((x.OrderNumber != null && numbers.Contains(x.OrderNumber))
                        || (x.TransactionDate >= start && x.TransactionDate < end
                            && (x.OrderNumber == null || !db.Orders.Any(order => order.TenantId == tenantId && order.ConnectionId == storeId && order.OrderNumber == x.OrderNumber)))))
                .Select(x => new ReportFinanceRow
                {
                    ConnectionId = x.ConnectionId,
                    Source = x.Source,
                    ExternalId = x.ExternalId,
                    RequestedType = x.RequestedType,
                    TransactionType = x.TransactionType,
                    Category = x.Category,
                    TransactionDate = x.TransactionDate,
                    OrderNumber = x.OrderNumber,
                    Barcode = x.Barcode,
                    Debt = x.Debt,
                    Credit = x.Credit,
                    CommissionAmount = x.CommissionAmount,
                    PaymentOrderId = x.PaymentOrderId
                })
                .ToListAsync(cancellationToken);
            rows.AddRange(await AllocateFeesAsync(tenantId, storeId, storeRows, numbers, start, end, cancellationToken));
            // The cargo invoices counted by date: their lines of orders the panel does not know count with them, and an
            // invoice that has lines counts through them instead of as a whole.
            var datedInvoices = storeRows.Where(x => TrendyolFinanceCategorizer.IsCargoInvoice(x.Source, x.RequestedType, x.Category))
                .Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).ToArray();
            items.AddRange(await db.MarketplaceCargoInvoiceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId
                    && ((x.OrderNumber != null && numbers.Contains(x.OrderNumber))
                        || (datedInvoices.Contains(x.InvoiceSerialNumber)
                            && (x.OrderNumber == null || !db.Orders.Any(order => order.TenantId == tenantId && order.ConnectionId == storeId && order.OrderNumber == x.OrderNumber)))))
                .Select(x => new ReportCargoItemRow { ConnectionId = x.ConnectionId, InvoiceSerialNumber = x.InvoiceSerialNumber, OrderNumber = x.OrderNumber, ShipmentPackageType = x.ShipmentPackageType, Amount = x.Amount })
                .ToListAsync(cancellationToken));
            if (datedInvoices.Length > 0)
                foreach (var serial in await db.MarketplaceCargoInvoiceItems.AsNoTracking()
                             .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId && datedInvoices.Contains(x.InvoiceSerialNumber))
                             .Select(x => x.InvoiceSerialNumber).Distinct().ToListAsync(cancellationToken))
                    itemized.Add((storeId, serial));

            if (await CargoTariffAsync(db, tenantId, storeId, end, cancellationToken) is { } sample) samples[storeId] = sample;
        }
        return new(rows, items, itemized, states, samples);
    }

    // The store's records with every per-sale fee invoice (service fee, withholding tax) of a read payout replaced by its
    // shares in the payout's orders (ReportFeeAllocation): the shares of the period's orders, also from invoices dated
    // outside the period (an October order's fee billed on a November payment day), and, for an invoice dated in the
    // period, the shares of orders the panel never imported. The shares of the panel's orders of another period count
    // in that period. An invoice that cannot be split stays as it is.
    private async Task<List<ReportFinanceRow>> AllocateFeesAsync(Guid tenantId, Guid storeId, List<ReportFinanceRow> storeRows, string[] numbers,
        DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var periodNumbers = numbers.ToHashSet(StringComparer.Ordinal);
        // The payouts that paid an order of the period, and their fee invoices not read with the period's dates.
        var paying = storeRows.Where(x => x.Source == TrendyolFinanceRequestContract.Settlements && x.OrderNumber is not null
                && periodNumbers.Contains(x.OrderNumber) && !string.IsNullOrEmpty(x.PaymentOrderId))
            .Select(x => x.PaymentOrderId!).Distinct(StringComparer.Ordinal).ToArray();
        var loaded = storeRows.Select(x => (x.Source, x.ExternalId)).ToHashSet();
        var invoices = storeRows.Where(ReportFeeAllocation.IsPerSaleInvoice).ToList();
        if (paying.Length > 0)
            invoices.AddRange((await db.MarketplaceFinanceTransactions.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId && x.Source == TrendyolFinanceRequestContract.OtherFinancials
                        && x.OrderNumber == null && x.PaymentOrderId != null && paying.Contains(x.PaymentOrderId)
                        && (x.Category == MarketplaceFinanceCategories.ServiceFee || x.Category == MarketplaceFinanceCategories.Stoppage))
                    .Select(x => new ReportFinanceRow
                    {
                        ConnectionId = x.ConnectionId,
                        Source = x.Source,
                        ExternalId = x.ExternalId,
                        RequestedType = x.RequestedType,
                        TransactionType = x.TransactionType,
                        Category = x.Category,
                        TransactionDate = x.TransactionDate,
                        Debt = x.Debt,
                        Credit = x.Credit,
                        PaymentOrderId = x.PaymentOrderId
                    })
                    .ToListAsync(cancellationToken))
                .Where(x => !loaded.Contains((x.Source, x.ExternalId))));
        if (invoices.Count == 0) return storeRows;

        var payouts = invoices.Select(x => x.PaymentOrderId!).Distinct(StringComparer.Ordinal).ToArray();
        var payoutLines = await db.MarketplaceFinanceTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId && x.Source == TrendyolFinanceRequestContract.Settlements
                && x.PaymentOrderId != null && payouts.Contains(x.PaymentOrderId) && x.OrderNumber != null
                && (x.RequestedType == "Sale" || x.RequestedType == "Return"))
            .Select(x => new ReportPayoutLine(x.ConnectionId, x.PaymentOrderId!, x.OrderNumber!, x.ShipmentPackageId, x.RequestedType, x.Country, x.Debt, x.Credit))
            .ToListAsync(cancellationToken);

        var split = invoices.Select(invoice => (Invoice: invoice, Shares: ReportFeeAllocation.Split(invoice, payoutLines))).ToList();
        // Which of the other orders the panel knows (their shares count in their own period).
        var others = split.Where(x => x.Shares is not null).SelectMany(x => x.Shares!).Select(x => x.OrderNumber!)
            .Where(number => !periodNumbers.Contains(number)).Distinct(StringComparer.Ordinal).ToArray();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in others.Chunk(2_000))
            known.UnionWith(await db.Orders.AsNoTracking()
                .Where(order => order.TenantId == tenantId && order.ConnectionId == storeId && chunk.Contains(order.OrderNumber))
                .Select(order => order.OrderNumber)
                .ToListAsync(cancellationToken));

        var replaced = split.Where(x => x.Shares is not null).Select(x => (x.Invoice.Source, x.Invoice.ExternalId)).ToHashSet();
        var result = storeRows.Where(x => !replaced.Contains((x.Source, x.ExternalId))).ToList();
        foreach (var (invoice, shares) in split)
        {
            if (shares is null) continue;
            var dated = invoice.TransactionDate >= start && invoice.TransactionDate < end;
            result.AddRange(shares.Where(share => periodNumbers.Contains(share.OrderNumber!) || dated && !known.Contains(share.OrderNumber!)));
        }
        return result;
    }

    // A Trendyol store's cargo tariff, for orders it has not billed cargo for yet (and a price not sold yet): the shipping
    // lines of its cargo invoices dated in the 30 days before the end (60 when none was dated then).
    internal static async Task<ReportCargoSample?> CargoTariffAsync(AppDbContext db, Guid tenantId, Guid storeId, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var sampleFrom = end.AddDays(-30);
        if (!await db.MarketplaceFinanceTransactions.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == storeId
                && x.Source == TrendyolFinanceRequestContract.OtherFinancials && x.RequestedType == TrendyolFinanceCategorizer.DeductionInvoices
                && x.Category == MarketplaceFinanceCategories.Cargo && x.TransactionDate >= sampleFrom && x.TransactionDate < end, cancellationToken))
            sampleFrom = end.AddDays(-60);
        var sampleSerials = db.MarketplaceFinanceTransactions
            .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId && x.Source == TrendyolFinanceRequestContract.OtherFinancials
                && x.RequestedType == TrendyolFinanceCategorizer.DeductionInvoices && x.Category == MarketplaceFinanceCategories.Cargo
                && x.TransactionDate >= sampleFrom && x.TransactionDate < end)
            .Select(x => x.ExternalId);
        var shipping = (await db.MarketplaceCargoInvoiceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == storeId && x.OrderNumber != null && sampleSerials.Contains(x.InvoiceSerialNumber))
                .Select(x => new ReportCargoItemRow { ConnectionId = x.ConnectionId, InvoiceSerialNumber = x.InvoiceSerialNumber, OrderNumber = x.OrderNumber, ShipmentPackageType = x.ShipmentPackageType, Amount = x.Amount })
                .ToListAsync(cancellationToken))
            .Where(x => !x.IsReturn)
            .ToList();
        return ReportCargoSample.Tariff(shipping.Select(x => (x.Amount, x.OrderNumber)).ToList());
    }

    private async Task<ReportCatalogNames> CatalogNamesAsync(Guid tenantId, IReadOnlyList<ReportLineRow> lines, CancellationToken cancellationToken)
    {
        var brandIds = lines.Where(x => x.BrandId != null).Select(x => x.BrandId!.Value).Distinct().ToArray();
        var categoryIds = lines.Where(x => x.CategoryId != null).Select(x => x.CategoryId!.Value).Distinct().ToArray();
        var brands = brandIds.Length == 0 ? [] : await db.Brands.AsNoTracking().Where(x => x.TenantId == tenantId && brandIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var categories = categoryIds.Length == 0 ? [] : await db.Categories.AsNoTracking().Where(x => x.TenantId == tenantId && categoryIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        return new(brands, categories);
    }

    // Stok raporu: the catalogue's variants with their MAIN stock and offers, and the units kept per variant in the period
    // and in the last ReportStock.DeadDays days, worked out as the sales report works them out (same orders, same returns).
    public async Task<ServiceResult<ReportInventoryView>> StockAsync(Guid tenantId, ReportStockQuery query, CancellationToken cancellationToken)
    {
        var timezoneId = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Timezone).SingleOrDefaultAsync(cancellationToken);
        var timezone = DashboardReadService.ResolveTimezone(timezoneId);
        var now = timeProvider.GetUtcNow();
        var resolved = DashboardPerformancePeriod.Resolve(query.From, query.To, now, timezone);
        if (!resolved.Succeeded) return ServiceResult<ReportInventoryView>.Fail(resolved.Error!.Code, resolved.Error.Message, resolved.Error.Status);
        var period = resolved.Value!;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timezone).Date);
        var recent = DashboardPerformancePeriod.Resolve(today.AddDays(1 - ReportStock.DeadDays), today, now, timezone).Value!;

        var excluded = DashboardRevenuePolicy.ExcludedConnectionStatuses;
        var channels = (await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !excluded.Contains(x.Status))
                .OrderBy(x => x.PlatformCode).ThenBy(x => x.DisplayName)
                .Select(x => new { x.Id, x.PlatformCode, x.DisplayName, x.Environment })
                .ToListAsync(cancellationToken))
            .Select(x => new ReportChannelView(x.Id, x.PlatformCode.Trim().ToUpperInvariant(), x.DisplayName, ReportBuckets.IsTestEnvironment(x.Environment)))
            .ToList();
        var platformFilter = query.PlatformCodes?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var filtered = platformFilter is { Count: > 0 };
        var allowed = channels
            .Where(x => query.IncludeTest || !x.IsTest)
            .Where(x => !filtered || platformFilter!.Contains(x.PlatformCode))
            .ToDictionary(x => x.Id);
        var allowedIds = allowed.Keys.ToArray();

        // The products the product list shows (VisibleProducts there): not only linked to removed stores. A draft is not
        // counted in the stock figures (ProductDraftPolicy).
        var products = db.Products.AsNoTracking().Where(product => product.TenantId == tenantId && product.Status != ProductStatus.Draft
            && (!db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ProductId == product.Id)
                || db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ProductId == product.Id
                    && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == link.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))));
        var variantRows = await (from variant in db.ProductVariants.AsNoTracking()
                                 join product in products on new { variant.TenantId, Id = variant.ProductId } equals new { product.TenantId, product.Id }
                                 join category in db.Categories.AsNoTracking() on new { product.TenantId, Id = product.CategoryId } equals new { category.TenantId, Id = (Guid?)category.Id } into categories
                                 from category in categories.DefaultIfEmpty()
                                 where variant.TenantId == tenantId
                                 select new
                                 {
                                     variant.Id,
                                     variant.ProductId,
                                     product.Title,
                                     variant.ModelCode,
                                     Category = category == null ? null : category.Name,
                                     variant.OptionSignature,
                                     variant.Status,
                                     variant.CostPrice,
                                     PanelPrice = variant.DefaultSalePrice ?? product.DefaultSalePrice,
                                     product.VatRate,
                                     variant.CreatedAt,
                                     OnHand = db.InventoryItems.Where(item => item.TenantId == tenantId && item.VariantId == variant.Id && item.LocationCode == "MAIN").Sum(item => (decimal?)item.OnHand) ?? 0m,
                                     Available = db.InventoryItems.Where(item => item.TenantId == tenantId && item.VariantId == variant.Id && item.LocationCode == "MAIN").Sum(item => (decimal?)item.Available) ?? 0m
                                 }).ToListAsync(cancellationToken);
        var optionRows = await (from assignment in db.VariantOptionValues.AsNoTracking()
                                join option in db.ProductOptions.AsNoTracking() on new { assignment.TenantId, assignment.OptionId } equals new { option.TenantId, OptionId = option.Id }
                                join value in db.ProductOptionValues.AsNoTracking() on new { assignment.TenantId, assignment.OptionValueId } equals new { value.TenantId, OptionValueId = value.Id }
                                where assignment.TenantId == tenantId && (option.NormalizedKey == "RENK" || option.NormalizedKey == "BEDEN")
                                select new { assignment.VariantId, option.NormalizedKey, value.Label }).ToListAsync(cancellationToken);
        var colors = optionRows.Where(x => x.NormalizedKey == "RENK").GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First().Label);
        var sizes = optionRows.Where(x => x.NormalizedKey == "BEDEN").GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => x.First().Label);
        // The panel price as the product editor shows it (CatalogPricePolicy): the variant's own, else its card's, else —
        // for a card imported from a marketplace that never had one — the variant's first offer, active ones first.
        var firstOfferPrice = (await db.ChannelOffers.AsNoTracking()
                .Where(x => x.TenantId == tenantId)
                .OrderByDescending(x => x.Status == "ACTIVE").ThenBy(x => x.Id)
                .Select(x => new { x.VariantId, x.SalePrice })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.VariantId).ToDictionary(x => x.Key, x => (decimal?)x.First().SalePrice);
        var variants = variantRows.Select(x => new ReportStockVariantRow(x.Id, x.ProductId, x.Title, x.ModelCode, x.Category, colors.GetValueOrDefault(x.Id), sizes.GetValueOrDefault(x.Id),
            x.OptionSignature, x.Status == ProductStatus.Active, x.CostPrice, x.VatRate, x.OnHand, x.Available, x.CreatedAt,
            x.PanelPrice ?? firstOfferPrice.GetValueOrDefault(x.Id))).ToList();
        var offers = await db.ChannelOffers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && allowedIds.Contains(x.ConnectionId))
            .Select(x => new ReportStockOfferRow(x.VariantId, x.ConnectionId, x.SalePrice, x.Currency, x.Status))
            .ToListAsync(cancellationToken);

        var periodSold = await KeptUnitsAsync(tenantId, allowed, new ReportPeriod(period.From, period.To, period.CurrentStart, period.CurrentEnd), timezone, cancellationToken);
        var recentSold = await KeptUnitsAsync(tenantId, allowed, new ReportPeriod(recent.From, recent.To, recent.CurrentStart, recent.CurrentEnd), timezone, cancellationToken);
        var report = ReportStock.Build(variants, offers, allowed, filtered, periodSold, recentSold, period.From, period.To, today);

        var imageProducts = report.TopCapital.Concat(report.Dead).Select(x => x.ProductId).Distinct().ToArray();
        var media = await (from item in db.ProductMedia.AsNoTracking()
                           join asset in db.FileAssets.AsNoTracking() on new { item.TenantId, item.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                           where item.TenantId == tenantId && imageProducts.Contains(item.ProductId) && item.Status == "ACTIVE" && asset.Status == "ACTIVE"
                               && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                           orderby item.SortOrder, item.Id
                           select new { item.ProductId, item.VariantId, asset.Id, asset.Classification, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        ReportStockItemView Pictured(ReportStockItemView item)
        {
            var picked = (item.VariantId is { } variantId ? media.FirstOrDefault(x => x.VariantId == variantId) : null)
                ?? media.FirstOrDefault(x => x.ProductId == item.ProductId && x.VariantId == null) ?? media.FirstOrDefault(x => x.ProductId == item.ProductId);
            return picked is null ? item : item with { ImageUrl = CatalogMediaDisplay.Url(picked.Id, picked.Classification, picked.Url) };
        }
        return ServiceResult<ReportInventoryView>.Ok(report with
        {
            GeneratedAt = now,
            TopCapital = report.TopCapital.Select(Pictured).ToList(),
            Dead = report.Dead.Select(Pictured).ToList(),
            Channels = channels
        });
    }

    // The units kept (sold less returned) per variant in a period, from the sales report's own products.
    private async Task<Dictionary<Guid, decimal>> KeptUnitsAsync(Guid tenantId, IReadOnlyDictionary<Guid, ReportChannelView> allowed, ReportPeriod period, TimeZoneInfo timezone,
        CancellationToken cancellationToken)
    {
        var allowedIds = allowed.Keys.ToArray();
        var input = new ReportInput(period, ReportBuckets.Day, timezone, allowed, await OrderRowsAsync(tenantId, allowedIds, period, cancellationToken),
            new Dictionary<Guid, ReportOrderExtra>(), await LineRowsAsync(tenantId, allowedIds, period, cancellationToken), new Dictionary<Guid, ReportLineExtra>(), [], [],
            new ReportCatalogNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>()), false);
        return ReportBuilder.Products(ReportBuilder.Facts(input).Orders, input.Names)
            .Where(x => x.VariantId != null)
            .ToDictionary(x => x.VariantId!.Value, x => x.SoldQuantity - x.ReturnedQuantity);
    }

    // Set through an object initializer, not a constructor, so the queries composed on it can still be translated.
    internal sealed class PeriodOrder
    {
        public Order Order { get; init; } = null!;
        public DateTimeOffset RealAt { get; init; }
    }
}
