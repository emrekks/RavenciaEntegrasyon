using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 5 (16 Nov 2026): the shared pieces of the job goldens written before a job family moves out of the
// processor. A run goes through ProcessAsync on a fixed start (GoldenClock steps one second per reading, so a time budget
// ends); every marketplace call is recorded by GoldenMarketplace with its idempotency key and its arguments as JSON (the
// exact command a write sends); the tenant's rows of the named tables are dumped by GoldenStore with every scalar column.
// Ids (any GUID, also inside text and in the 32-digit form) become names in their order of first appearance, times
// become offsets from the start, hashes become "<hash>", and rows are ordered by their text in memory with
// StringComparer.Ordinal, so the golden does not depend on the database's collation (C locally, en_US on CI).
internal static partial class JobGolden
{
    public static readonly DateTimeOffset Start = new(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    public static string Time(DateTimeOffset value)
    {
        // A database default (now()) or the test's real clock: not the golden's business.
        if (Math.Abs((value - DateTimeOffset.UtcNow).TotalDays) < 2) return "<now>";
        var offset = value.ToUniversalTime() - Start;
        return offset < TimeSpan.Zero ? "T-" + (-offset).ToString("c", CultureInfo.InvariantCulture) : "T+" + offset.ToString("c", CultureInfo.InvariantCulture);
    }

    public static JsonNode? Value(object? value) => value switch
    {
        null => null,
        DateTimeOffset time => JsonValue.Create(Time(time)),
        DateTime time => JsonValue.Create(Time(new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)))),
        Guid id => JsonValue.Create(id.ToString()),
        Enum item => JsonValue.Create(item.ToString()),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        byte[] bytes => JsonValue.Create($"<{bytes.Length} bytes>"),
        int or long or short or decimal or double or float or uint or ulong => JsonSerializer.SerializeToNode(value, value.GetType(), Options),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), Options)
    };

    public static string Json(object? value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);

    // Compares the summary with Fixtures/<goldenPath> after naming the ids: the known ones by the given names, the others
    // id1, id2, ... in the order they first appear.
    public static void Matches(string goldenPath, object summary, IReadOnlyDictionary<Guid, string> known)
    {
        var text = JsonSerializer.Serialize(summary, summary.GetType(), Options);
        var names = new Dictionary<Guid, string>(known);
        var labelled = GuidPattern().Replace(text, match =>
        {
            var id = Guid.Parse(match.Value);
            if (!names.TryGetValue(id, out var name)) names[id] = name = $"id{names.Count - known.Count + 1}";
            return name;
        });
        ContractFixture.MatchesGolden(goldenPath, JsonNode.Parse(labelled));
    }

    [GeneratedRegex("(?<![0-9a-fA-F])[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}(?![0-9a-fA-F])")]
    private static partial Regex GuidPattern();

    // Every table of the tenant (each entity type with a TenantId column) that holds rows, with every mapped scalar column,
    // ordered by their text with the known ids named and the others masked. The outbox is summed by kind instead (its rows
    // differ only by ids the run made).
    public static async Task<object> TablesAsync(AppDbContext db, Guid tenantId, IReadOnlyDictionary<Guid, string> known)
    {
        var tables = new List<object>();
        var entityTypes = db.Model.GetEntityTypes()
            .Where(entity => entity.FindProperty("TenantId") is not null && !entity.IsOwned() && entity.FindPrimaryKey() is not null
                && !entity.HasSharedClrType && entity.ClrType != typeof(IntegrationOutboxEvent))
            .OrderBy(entity => entity.ClrType.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var entity in entityTypes)
        {
            var rows = (IList<object>)await (Task<List<object>>)typeof(JobGolden).GetMethod(nameof(RowsAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(entity.ClrType).Invoke(null, [db, tenantId])!;
            if (rows.Count == 0) continue;
            var properties = entity.GetProperties().Where(property => property.PropertyInfo is not null).OrderBy(property => property.Name, StringComparer.Ordinal).ToList();
            tables.Add(new
            {
                Table = entity.ClrType.Name,
                Rows = rows.Select(row => Row(row, properties)).OrderBy(row => SortKey(row.ToJsonString(), known), StringComparer.Ordinal).ToList()
            });
        }
        var outbox = await db.IntegrationOutboxEvents.AsNoTracking().Where(x => x.TenantId == tenantId)
            .Select(x => new { x.ResourceType, x.AggregateType, x.OperationType }).ToListAsync();
        tables.Add(new
        {
            Table = "IntegrationOutboxEvent (counted)",
            Rows = outbox.GroupBy(x => $"{x.ResourceType} {x.AggregateType} {x.OperationType}").OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key} x{group.Count()}").ToList()
        });
        return tables;
    }

    private static string SortKey(string text, IReadOnlyDictionary<Guid, string> known) =>
        GuidPattern().Replace(text, match => known.TryGetValue(Guid.Parse(match.Value), out var name) ? name : "#");

    private static JsonObject Row(object row, IEnumerable<IProperty> properties)
    {
        var json = new JsonObject();
        foreach (var property in properties)
        {
            var value = property.PropertyInfo!.GetValue(row);
            // A run's duration is measured on the real clock (a stopwatch): only whether it was written is pinned.
            json[property.Name] = property.Name.EndsWith("Hash", StringComparison.Ordinal) && value is string { Length: > 0 }
                ? JsonValue.Create("<hash>")
                : property.Name.EndsWith("DurationMs", StringComparison.Ordinal) && value is not null
                    ? JsonValue.Create("<duration>")
                    : Value(value);
        }
        return json;
    }

    private static async Task<List<object>> RowsAsync<T>(AppDbContext db, Guid tenantId) where T : class =>
        [.. await db.Set<T>().AsNoTracking().Where(row => EF.Property<Guid>(row, "TenantId") == tenantId).ToListAsync()];
}

// One second later at every reading, so a loop that runs until a time budget is spent ends.
internal sealed class GoldenClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;
    public void Advance(TimeSpan by) => now += by;
    public override DateTimeOffset GetUtcNow() => now += TimeSpan.FromSeconds(1);
}

// A clock that never moves (the AppDbContext's own stamps), so they do not shift the processor's clock readings.
internal sealed class FrozenClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

// Every marketplace port the processor holds. Each call is recorded as "<port.Method> key=<idempotency key>
// [op=<operation>] [flags] <arguments as JSON>" and answered from Answers by the same "<port.Method>" name; an
// unscripted call fails with GOLDEN_UNSCRIPTED (class NotSupported), which the golden then shows.
internal sealed class GoldenMarketplace : IOrderPort, IOrderPackageReadPort, IReturnPort, IHepsiburadaInvoiceStatusPort, IConnectionPort, IProductPort, IInventoryPricePort, IReferenceDataPort, IListingCodeCheckPort
{
    public List<string> Calls { get; } = [];
    public Dictionary<string, Func<object?[], object>> Answers { get; } = new(StringComparer.Ordinal);

    public static AdapterError Error(AdapterErrorClass errorClass, string code, int? status = null) => new(errorClass, code, $"golden: {code}", status, null, null);

    private Task<AdapterResult<T>> Call<T>(string method, AdapterContext context, params object?[] args)
    {
        if (Calls.Count >= 5_000) throw new InvalidOperationException("golden: runaway calls");
        var flags = new List<string>();
        if (context.Operation != IntegrationOperation.Manual) flags.Add($"op={context.Operation}");
        if (context.IsStageCapabilityProbe) flags.Add("stageProbe");
        if (context.IsOneTimeInvoiceDeliveryAuthorized) flags.Add($"oneTimeInvoice={context.OneTimeInvoiceDeliveryOrderNumber}");
        if (context.IsAutomaticInvoiceMarketplaceDelivery) flags.Add("autoInvoiceDelivery");
        if (context.IsExplicitManualInvoiceDelivery) flags.Add("manualInvoiceDelivery");
        if (context.IsExchangeInvoiceResend) flags.Add("exchangeResend");
        var arguments = string.Join(" ", args.Select(JobGolden.Json));
        Calls.Add($"{method} key={context.IdempotencyKey}{(flags.Count == 0 ? "" : " " + string.Join(" ", flags))} {arguments}".TrimEnd());
        return Task.FromResult(Answers.TryGetValue(method, out var answer)
            ? (AdapterResult<T>)answer(args)
            : AdapterResult<T>.Failure(Error(AdapterErrorClass.NotSupported, "GOLDEN_UNSCRIPTED")));
    }

    // IOrderPort
    public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Call<AdapterPageResult<RemoteOrder>>("orders.Poll", context, window, page);
    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => Call<RemoteOrder>("orders.Get", context, externalOrderId);
    public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, OrderLookupHint? hint, CancellationToken cancellationToken) => Call<RemoteOrder>("orders.GetHinted", context, externalOrderId, hint);
    public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => Call<RemoteOrderPackage>("orders.GetPackage", context, externalPackageId);
    public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, DateTimeOffset? packageStatusOccurredAt, CancellationToken cancellationToken) => Call<RemoteOrderPackage>("orders.GetPackageAt", context, externalPackageId, packageStatusOccurredAt);
    public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, PackageLookup lookup, CancellationToken cancellationToken) => Call<RemoteOrderPackage>("orders.GetPackageLookup", context, externalPackageId, lookup);
    public Task<AdapterResult<IReadOnlyList<RemoteOrder>>> GetShipmentPackagesAsync(AdapterContext context, IReadOnlyList<string> externalPackageIds, OrderLookupHint? hint, CancellationToken cancellationToken) => Call<IReadOnlyList<RemoteOrder>>("orders.GetPackages", context, externalPackageIds, hint);
    public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => Call<PackageActionResult>("orders.PackageAction", context, command);
    public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => Call<IReadOnlyList<RemoteCargoCompany>>("orders.CargoCompanies", context, externalPackageId);
    public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => Call<IReadOnlyList<RemotePackageableLine>>("orders.PackageableLines", context, externalLineItemId);
    public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => Call<CreateOrderPackageResult>("orders.CreatePackage", context, command);
    public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => Call<bool>("orders.CreateCommonLabel", context, request);
    public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => Call<CommonLabelDocument>("orders.GetCommonLabel", context, cargoTrackingNumber, format);
    public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Call<StageTestOrderResult>("orders.CreateStageTestOrder", context, barcode);

    // IOrderPackageReadPort
    public Task<AdapterResult<AdapterPageResult<RemoteOrderPackage>>> PollPackagesAsync(AdapterContext context, PackagePollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Call<AdapterPageResult<RemoteOrderPackage>>("packages.Poll", context, window, page);
    public Task<AdapterResult<PackageTrackingStatusSnapshot>> GetPackageTrackingInfoAsync(AdapterContext context, string packageNumber, CancellationToken cancellationToken) => Call<PackageTrackingStatusSnapshot>("packages.Tracking", context, packageNumber);

    // IReturnPort
    public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => Call<AdapterPageResult<RemoteReturnClaim>>("returns.Poll", context, window, page);
    Task<AdapterResult<RemoteReturnClaim>> IReturnPort.GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) => Call<RemoteReturnClaim>("returns.Get", context, externalReturnId);
    public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => Call<IReadOnlyList<ReturnIssueReason>>("returns.IssueReasons", context);
    public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => Call<ReturnActionResult>("returns.Execute", context, command);
    public Task<AdapterResult<IReadOnlyList<ReturnShipmentCompany>>> ReturnShipmentCompaniesAsync(AdapterContext context, CancellationToken cancellationToken) => Call<IReadOnlyList<ReturnShipmentCompany>>("returns.ShipmentCompanies", context);

    // IHepsiburadaInvoiceStatusPort
    public Task<AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>> ListMissingInvoicePackagesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken) => Call<AdapterPageResult<RemoteMissingInvoicePackage>>("missingInvoices.List", context, page);

    // IConnectionPort
    public Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken) => Call<ConnectionIdentity>("connection.Test", context);
    public Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken) => Call<IReadOnlyList<CapabilityEvidence>>("connection.Discover", context);

    // IProductPort
    public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => Call<AdapterPageResult<RemoteProduct>>("products.List", context, page, filter);
    public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => Call<AdapterPageResult<RemoteCatalogProduct>>("products.ListCatalog", context, page, filter);
    public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => Call<RemoteOperationRef>("products.Create", context, publication);
    public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Call<RemoteOperationRef>("products.UpdateUnapproved", context, publication);
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Call<RemoteOperationRef>("products.UpdateApprovedContent", context, publication);
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Call<RemoteOperationRef>("products.UpdateApprovedVariants", context, publication);
    public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => Call<RemoteOperationRef>("products.UpdateApprovedDelivery", context, publication);
    public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => Call<RemoteOperationStatus>("products.GetOperation", context, externalOperationId);
    public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => Call<RemotePublicationStatus>("products.GetPublicationStatus", context, barcode);
    public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => Call<RemoteOperationRef>("products.Archive", context, payloadJson);

    // IInventoryPricePort
    public Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => Call<RemoteOperationRef>("inventory.Push", context, payloadJson);

    // IReferenceDataPort
    public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken) => Call<AdapterPageResult<RemoteReferenceItem>>("references.Read", context, resource, page);

    // IListingCodeCheckPort
    public Task<AdapterResult<bool?>> HoldsCodeAsync(AdapterContext context, string code, CancellationToken cancellationToken) => Call<bool?>("products.HoldsCode", context, code);
}
