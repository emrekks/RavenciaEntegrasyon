using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// Y5/O7 (scan 23 Nov 2026): the owner deleted every product on 17 Nov and imported again under new ids; the issues keyed by
// the old variant ids were never named again, so about 1,350 of them stayed open beside the real work (1,041 Web Color,
// 309 PttAVM variants) until the 30-day stale pass.
//
// The rule (review 23 Nov 2026, after a first version closed real warnings): an issue is resolved here in two cases only.
// SubjectGone: the thing its key names (a panel variant, a connection) is no longer in the database. ConditionCleared: the
// guard the writer itself checks before it raises the warning no longer holds (the model product it named has its product
// link; the listing check's observe-only runs are over). Nothing is inferred from age, from a missing field, or from a
// guess that it is "probably fixed". Every rule names one issue code; a code without a rule here is never touched,
// whatever its key looks like, and a new code stays out until a rule and its test are written. Warnings whose condition
// only the marketplace's data can judge (a code clash over a listing's whole code set, a listing linked to the wrong panel
// variant, a failed import, a colour PttAVM does not list for a variant that still exists) are left to their producers,
// which resolve them on the next import of that listing.
//
// History is kept: the issue is resolved with the reason and the actor added to its summary, never deleted (the retention
// removes resolved issues a month later, as before). A later occurrence under the same key reopens it: IssueRecorder sets
// it Open with the writer's own summary again.
public static class OrphanedOperationalIssues
{
    public const string ResolvedStatus = nameof(IssueStatus.Resolved);

    // The two reasons a rule may give.
    public const string SubjectGone = "SUBJECT_GONE";
    public const string ConditionCleared = "CONDITION_CLEARED";

    public static string ReasonLabel(string reason) => reason == SubjectGone ? "konu artık yok" : "koşul artık geçerli değil";

    // The actors that run the pass: the worker's daily step, and the owner's deletes (a product, a connection, a reset).
    public const string MaintenanceActor = "sistem, günlük bakım";
    public const string DeleteActor = "sistem, silme sonrası";

    // A uuid in the D or N form (the writers use both), else NULL: the CASE keeps a key that does not hold one from the cast.
    private static string Uuid(string text) =>
        $$"""(CASE WHEN {{text}} ~ '^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$' THEN CAST({{text}} AS uuid) END)""";

    private static string Part(int index) => $"""split_part(i."DedupeKey", ':', {index})""";

    // What follows "<prefix><connection>:" in a key: the marketplace id the writer put there (it may hold ':' itself).
    private static string AfterConnection(string prefix) => $"""substring(i."DedupeKey" from {prefix.Length + 2} + length({Part(2)}))""";

    private static readonly string Connection = Uuid(Part(2));
    private static readonly string VariantAtThree = Uuid(Part(3));
    // A key whose third part is a variant id in the N form and nothing after it ({prefix}{connection}:{variant:N}).
    private const string Hex32AtThreeAlone = """split_part(i."DedupeKey", ':', 3) ~ '^[0-9a-f]{32}$' AND split_part(i."DedupeKey", ':', 4) = ''""";
    private const string Unmatched = "product-mapping-unmatched:";
    private static readonly string UnmatchedListing = AfterConnection(Unmatched);
    private static readonly string VariantGone = $"""
        {VariantAtThree} IS NOT NULL AND NOT EXISTS (SELECT 1 FROM catalog.product_variants v WHERE v."TenantId" = i."TenantId" AND v."Id" = {VariantAtThree})
        """;
    private static readonly string ConnectionGone = $"""
        {Connection} IS NOT NULL AND NOT EXISTS (SELECT 1 FROM integration.platform_connections c WHERE c."TenantId" = i."TenantId" AND c."Id" = {Connection} AND c."Status" <> 'DELETED')
        """;

    private static string KeyStarts(params string[] prefixes) => "(" + string.Join(" OR ", prefixes.Select(prefix => $"""i."DedupeKey" LIKE '{prefix}%'""")) + ")";

    public const string MappingUnmatchedCode = "PRODUCT_MAPPING_UNMATCHED";
    public const string ImportFailedCode = "PRODUCT_IMPORT_FAILED";
    public const string BarcodeConflictCode = "PRODUCT_BARCODE_CONFLICT";
    public const string ListingObserveCode = "LISTING_VERIFY_OBSERVE";
    public const string PttAvmCodeNotModelCode = "PTTAVM_PRODUCT_CODE_NOT_MODEL";

    public sealed record Rule(string Name, string Code, string Reason, string Detail, string Where)
    {
        // The whole match of the rule, the same text in the pass and in the prediction query: its one code, its condition.
        public string Match => $"""i."Code" = '{Code}' AND ({Where})""";

        // One bounded batch: the oldest open issues the rule matches (@tenant: one tenant, NULL: every tenant), resolved
        // with the reason and actor added to the summary; how many it resolved comes back. Built from constants only. EF
        // formats raw SQL as a composite format string, so the regex quantifiers' braces are doubled.
        public string UpdateSql => $"""
            WITH doomed AS (
                SELECT i."Id" FROM ops.operational_issues i
                WHERE i."Status" <> '{ResolvedStatus}' AND i."TenantId" IS NOT NULL
                  AND (CAST(@tenant AS uuid) IS NULL OR i."TenantId" = @tenant)
                  AND {Match.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal)}
                ORDER BY i."Id"
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            UPDATE ops.operational_issues i SET "Status" = '{ResolvedStatus}', "ResolvedAt" = @now, "Summary" = i."Summary" || @suffix
            FROM doomed WHERE i."Id" = doomed."Id"
            RETURNING i."Code" AS "Value"
            """;

        // What the resolved issue's summary gains: " · Otomatik kapatıldı (sistem, günlük bakım): konu artık yok — …".
        public string Suffix(string actor) => $" · Otomatik kapatıldı ({actor}): {ReasonLabel(Reason)} — {Detail}";
    }

    private static Rule VariantGoneRule(string name, string code, string keyCondition) =>
        new(name, code, SubjectGone, "kaydın panel varyantı silinmiş.", $"{keyCondition} AND {VariantGone}");

    private static Rule ConnectionGoneRule(string code, params string[] prefixes) =>
        new($"connection-gone:{code}", code, SubjectGone, "kaydın pazaryeri bağlantısı silinmiş.", $"{KeyStarts(prefixes)} AND {ConnectionGone}");

    // In order: a rule resolves what it matches before the next one runs, and the prediction query attributes each issue to
    // the first rule that matches it, the same way.
    public static readonly IReadOnlyList<Rule> Rules =
    [
        // product-web-color-mapping / product-web-color-value / product-attribute-value:{product}:{variant}:… (CatalogAttributeService):
        // the panel variant the colour or attribute value was to be set on.
        VariantGoneRule("variant-gone", "PRODUCT_WEBCOLOR_MAPPING_REQUIRED", KeyStarts("product-web-color-mapping:")),
        VariantGoneRule("variant-gone", "PRODUCT_WEBCOLOR_VALUE_UNMAPPED", KeyStarts("product-web-color-value:")),
        VariantGoneRule("variant-gone", "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED", KeyStarts("product-attribute-value:")),
        // stock-projection-offer-invalid:{connection:N}:{variant:N} (PriceInventoryOfferIssues.Key): the variant whose offer
        // the automatic send left out.
        .. PriceStockOfferIssueCodes.All.Select(code => VariantGoneRule("variant-gone", code, $"{KeyStarts(PriceInventoryOfferIssues.KeyPrefix)} AND {Hex32AtThreeAlone}")),
        // product-sync-model-code-conflict:{connection}:{variant:N} (CatalogImportService): the variant whose model code change clashed.
        VariantGoneRule("variant-gone", ModelCodeUniquenessPolicy.ChangeConflictCode, $"{KeyStarts("product-sync-model-code-conflict:")} AND {Hex32AtThreeAlone}"),
        // PttAVM keys its variant warnings by the panel variant (MapPttAvmProduct: no code, colour/size not listed, code taken):
        // product-mapping-unmatched:{connection}:{variant:N} on a PttAVM connection, summary written by that mapping.
        VariantGoneRule("variant-gone", MappingUnmatchedCode, $"""
            {KeyStarts(Unmatched)} AND {Hex32AtThreeAlone} AND i."Summary" LIKE 'PttAVM%'
            AND EXISTS (SELECT 1 FROM integration.platform_connections c WHERE c."TenantId" = i."TenantId" AND c."Id" = {Connection} AND c."PlatformCode" = 'PTTAVM')
            """),
        // product-mapping-unmatched:{connection}:main:… / group:… — the N11 / Pazarama model product whose variants fell on
        // several panel cards. MapExistingCatalogProduct raises it only while the listing has no product link (a variant id
        // never starts with main: or group:); the link exists now, so its own guard says it cannot hold.
        new("model-product-linked", MappingUnmatchedCode, ConditionCleared, "pazaryeri ürününün panel ürün bağlantısı artık var.", $"""
            {KeyStarts(Unmatched)} AND {Connection} IS NOT NULL AND ({UnmatchedListing} LIKE 'main:_%' OR {UnmatchedListing} LIKE 'group:_%')
            AND EXISTS (SELECT 1 FROM catalog.marketplace_product_links l WHERE l."TenantId" = i."TenantId" AND l."ConnectionId" = {Connection} AND l."ExternalId" = {UnmatchedListing})
            """),
        // listing-verify-observe:{connection}: ListingVerificationPolicy.ObservesOnly(platform, completedRuns) is false for
        // every run after the observe-only ones, and at least one such run has completed (completedRuns above the limit).
        new("observe-period-over", ListingObserveCode, ConditionCleared, "gözlem dönemi bitti; tarama durumları artık kendisi işaretliyor.", $$"""
            {{KeyStarts("listing-verify-observe:")}} AND {{Connection}} IS NOT NULL AND {{Part(3)}} = ''
            AND EXISTS (SELECT 1 FROM integration.platform_connections c WHERE c."TenantId" = i."TenantId" AND c."Id" = {{Connection}} AND c."PlatformCode" IN ('N11', 'PAZARAMA', 'PTTAVM'))
            AND EXISTS (SELECT 1 FROM integration.sync_cursors s WHERE s."TenantId" = i."TenantId" AND s."ConnectionId" = {{Connection}}
              AND s."ResourceType" = '{{ListingVerificationPolicy.StateCursorResource}}'
              AND COALESCE(CAST(substring(s."OpaqueCursor" from '"completedRuns":([0-9]{1,9})') AS int), 0) > {{ListingVerificationPolicy.ObserveOnlyRuns}})
            """),
        // An issue about one connection's listings whose connection is gone (Bağlantıyı Sil keeps the row as DELETED).
        ConnectionGoneRule(MappingUnmatchedCode, Unmatched, "pttavm-product-link:"),
        ConnectionGoneRule(ImportFailedCode, "product-sync-import:"),
        ConnectionGoneRule(BarcodeConflictCode, "product-sync-barcode-conflict:"),
        ConnectionGoneRule(ListingObserveCode, "listing-verify-observe:"),
        .. PriceStockOfferIssueCodes.All.Select(code => ConnectionGoneRule(code, PriceInventoryOfferIssues.KeyPrefix)),
        ConnectionGoneRule(ModelCodeUniquenessPolicy.ChangeConflictCode, "product-sync-model-code-conflict:"),
        ConnectionGoneRule(PttAvmCodeNotModelCode, "pttavm-product-code:")
    ];

    // The codes the pass may touch: every other code is left alone.
    public static IReadOnlySet<string> Codes { get; } = Rules.Select(rule => rule.Code).ToHashSet(StringComparer.Ordinal);

    // Resolves the open issues the rules match, of one tenant (null: every tenant), at most batchSize x maxBatches per rule;
    // subjectGoneOnly keeps to the SubjectGone rules (a delete). Returns how many per code and reason. Idempotent.
    public static async Task<IReadOnlyDictionary<(string Code, string Reason), int>> ResolveAsync(AppDbContext db, Guid? tenantId, DateTimeOffset now, int batchSize, int maxBatches, string actor, CancellationToken cancellationToken, bool subjectGoneOnly = false)
    {
        var resolvedAt = now.ToUniversalTime();
        var counts = new Dictionary<(string Code, string Reason), int>();
        foreach (var rule in Rules.Where(rule => !subjectGoneOnly || rule.Reason == SubjectGone))
        {
            for (var batch = 0; batch < maxBatches; batch++)
            {
                var codes = await db.Database.SqlQueryRaw<string>(rule.UpdateSql,
                    new NpgsqlParameter("tenant", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = tenantId is { } id ? (object)id : DBNull.Value },
                    new NpgsqlParameter<int>("batch", batchSize),
                    new NpgsqlParameter<DateTimeOffset>("now", resolvedAt),
                    new NpgsqlParameter<string>("suffix", rule.Suffix(actor))).ToListAsync(cancellationToken);
                if (codes.Count > 0) counts[(rule.Code, rule.Reason)] = counts.GetValueOrDefault((rule.Code, rule.Reason)) + codes.Count;
                if (codes.Count < batchSize) break;
            }
        }
        return counts;
    }

    // The summary mark every issue this pass resolved carries (Rule.Suffix), whoever ran it.
    public const string ResolvedMark = " · Otomatik kapatıldı (sistem, ";

    // The read-only check after a pass (.local-docs/veri-sagligi-verify.sql): every issue the pass resolved, by status, code
    // and the rule that matches it now (the same match text); one no rule matches any more is listed as such. Then the
    // issues still open, by code.
    public static string VerifySql(string? tenantCondition = null)
    {
        var cases = string.Join("\n", Rules.Select((rule, index) => $"""      WHEN {rule.Match} THEN '{index:D2} {rule.Name}|{rule.Reason}'"""));
        var tenant = tenantCondition is null ? "" : $" AND {tenantCondition}";
        return $"""
            -- Generated from OrphanedOperationalIssues.Rules (OrphanedOperationalIssues.VerifySql): read only.
            -- Every issue the orphaned-issue pass resolved, by status, code and the rule that matches it now; expected: each one
            -- Resolved and matched by a rule of its own code, none listed under NO RULE MATCHES NOW. Then what stays open.
            WITH passed AS (
              SELECT i.* FROM ops.operational_issues i
              WHERE i."TenantId" IS NOT NULL AND position('{ResolvedMark}' in i."Summary") > 0{tenant}),
            classified AS (
              SELECT i."Status" AS status, i."Code" AS code, i."ResolvedAt" AS resolved_at, CASE
            {cases}
                ELSE NULL END AS rule_reason
              FROM passed i)
            SELECT 'resolved by the pass' AS kind, status, code, COALESCE(split_part(rule_reason, '|', 2), 'NO RULE MATCHES NOW') AS reason,
                   COALESCE(split_part(rule_reason, '|', 1), '') AS rule, count(*) AS issues, min(resolved_at) AS first_resolved, max(resolved_at) AS last_resolved
            FROM classified GROUP BY 1, 2, 3, 4, 5
            UNION ALL
            SELECT 'open now', CAST(i."Status" AS text), i."Code", '', '', count(*), NULL, NULL
            FROM ops.operational_issues i WHERE i."Status" <> '{ResolvedStatus}' AND i."TenantId" IS NOT NULL{tenant} GROUP BY 2, 3
            ORDER BY 1 DESC, 2, 3, 4, 5;
            """;
    }

    // The read-only prediction (.local-docs/veri-sagligi-predict.sql): every open issue of the codes above, by code, reason
    // and rule, with the rules' own match text in their own order, so it counts what the pass resolves; and the ones it
    // leaves open, by code. tenantCondition narrows it to one tenant (the tests). perRuleCap is batchSize x maxBatches.
    public static string PredictSql(int perRuleCap, string? tenantCondition = null)
    {
        var cases = string.Join("\n", Rules.Select((rule, index) => $"""      WHEN {rule.Match} THEN '{index:D2} {rule.Name}|{rule.Reason}'"""));
        return $"""
            -- Generated from OrphanedOperationalIssues.Rules (OrphanedOperationalIssues.PredictSql): read only.
            -- What the first maintenance pass resolves (code x reason x rule; at most {perRuleCap} per rule a pass) and what stays open.
            WITH open_issues AS (
              SELECT i.* FROM ops.operational_issues i
              WHERE i."Status" <> '{ResolvedStatus}' AND i."TenantId" IS NOT NULL{(tenantCondition is null ? "" : $" AND {tenantCondition}")}),
            classified AS (
              SELECT i."Code" AS code, CASE
            {cases}
                ELSE NULL END AS rule_reason
              FROM open_issues i),
            by_rule AS (
              SELECT code, split_part(rule_reason, '|', 2) AS reason, split_part(rule_reason, '|', 1) AS rule, count(*) AS matched
              FROM classified WHERE rule_reason IS NOT NULL GROUP BY 1, 2, 3)
            SELECT 'resolves' AS kind, code, reason, rule, LEAST(matched, {perRuleCap}) AS issues FROM by_rule
            UNION ALL
            SELECT 'stays open', code, '', '', count(*) FROM classified WHERE rule_reason IS NULL GROUP BY code
            ORDER BY 1, 2, 3, 4;
            """;
    }
}
