using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// The one micro-export rule (MicroExportRule; owner's decision of 27 Nov 2026, finding C-11.9) in PostgreSQL: the
/// order list's Mikro İhracat and Fatura Bölgesi filters and the micro-export card read these; the label, the row mark
/// and the invoice read MicroExportRule.Classify in memory. MicroExportRuleTests run every case through both.
/// <para>Each order is read once (review O-1, 27 Nov 2026): its marketplace by one join, its snapshot detoasted once,
/// checked for <c>\u0000</c>, validated and decoded once as <c>json</c> (a snapshot with a <c>\uD…</c> escape is also
/// checked as <c>jsonb</c>, which refuses a lone surrogate the field read would fail on), and the top-level fields taken from that one
/// decode (<c>json_to_record</c>); the key count is one <c>regexp_count</c>. OFFSET 0 keeps PostgreSQL from inlining
/// the subqueries (which would decode again for every use). No stored column, no function created.</para>
/// </summary>
internal static class OrderMicroExportRules
{
    private static string Quoted(IEnumerable<string> values) => string.Join(",", values.Select(value => $"'{value}'"));

    private static string Field(string name) => $"\"{name}\" json";

    private static string Strings(IEnumerable<string> fields) =>
        $"NULLIF(concat_ws('|', {string.Join(", ", fields.Select(field => $"CASE WHEN left(r.\"{field}\"::text, 1) = '\"' THEN r.\"{field}\"::text END"))}), '')";

    // {0} the tenant, {1} the state (MicroExportState as a number). The patterns and lists are the rule's constants.
    private static readonly string StateSql = $"""
        SELECT b."Id" AS "Value"
        FROM (
            SELECT o."Id", o."StoreFrontCode" AS sf, c."PlatformCode" AS platform, (o."CustomerSnapshotJson" || '') AS t
            FROM sales.orders AS o
            LEFT JOIN integration.platform_connections AS c ON c."Id" = o."ConnectionId"
            WHERE o."TenantId" = {"{0}"}
            OFFSET 0) AS b
        CROSS JOIN LATERAL (
            SELECT CASE WHEN b.platform = '{MicroExportRule.Trendyol}' AND strpos(b.t, '{MicroExportRule.NulEscape}') = 0 AND pg_input_is_valid(b.t, 'json')
                             AND (strpos(b.t, '\ud') = 0 AND strpos(b.t, '\uD') = 0 OR pg_input_is_valid(b.t, 'jsonb'))
                        THEN b.t::json END AS j,
                   CASE WHEN b.platform = '{MicroExportRule.Trendyol}' THEN regexp_count(b.t, '{MicroExportRule.SignalKeyPattern}') END AS keys
            OFFSET 0) AS d
        LEFT JOIN LATERAL json_to_record(CASE WHEN json_typeof(d.j) = 'object' THEN d.j END)
            AS r({Field("micro")}, {Field("3pByTrendyol")}, {string.Join(", ", MicroExportRule.TypeFields.Concat(MicroExportRule.NameFields).Select(Field))}) ON true
        CROSS JOIN LATERAL (
            SELECT r.micro::text AS m, r."3pByTrendyol"::text AS p,
                   {Strings(MicroExportRule.TypeFields)} AS types, {Strings(MicroExportRule.NameFields)} AS names
            OFFSET 0) AS f
        WHERE (CASE
            WHEN b.platform IS NULL THEN {(int)MicroExportState.Unknown}
            WHEN b.platform <> '{MicroExportRule.Trendyol}' THEN {(int)MicroExportState.Domestic}
            WHEN json_typeof(d.j) IS DISTINCT FROM 'object' THEN {(int)MicroExportState.Unknown}
            WHEN d.keys <> (CASE WHEN f.m IS NULL THEN 0 ELSE 1 END) + (CASE WHEN f.p IS NULL THEN 0 ELSE 1 END) THEN {(int)MicroExportState.Unknown}
            WHEN f.m IS NULL AND f.p IS DISTINCT FROM 'true' THEN {(int)MicroExportState.Unknown}
            WHEN f.m = 'true' AND f.p = 'true' THEN {(int)MicroExportState.Unknown}
            WHEN (f.m = 'true' AND (f.p IS NULL OR f.p = 'false')) OR (f.p = 'true' AND (f.m IS NULL OR f.m = 'false'))
                THEN CASE WHEN upper(b.sf COLLATE "C") = 'TR' THEN {(int)MicroExportState.Unknown} ELSE {(int)MicroExportState.MicroExport} END
            WHEN f.m = 'false' AND (f.p IS NULL OR f.p = 'false')
                THEN CASE WHEN upper(b.sf COLLATE "C") IN ({Quoted(MicroExportRule.MicroExportStorefronts)})
                            OR f.types ~ '{MicroExportRule.TypeWordPattern}'
                            OR (f.names ~ '{MicroExportRule.Pm3Pattern}' AND f.names ~ '{MicroExportRule.ArvatoPattern}')
                          THEN {(int)MicroExportState.Unknown} ELSE {(int)MicroExportState.Domestic} END
            ELSE {(int)MicroExportState.Unknown} END) = {"{1}"}
        """;

    /// <summary>The tenant's orders the rule gives this answer, as a subquery the database runs.</summary>
    internal static IQueryable<Guid> Ids(AppDbContext db, Guid tenantId, MicroExportState state) =>
        db.Database.SqlQuery<Guid>(FormattableStringFactory.Create(StateSql, tenantId, (int)state));

    /// <summary>The orders the rule calls micro export: the "Mikro İhracat" region, filter and card.</summary>
    internal static Expression<Func<Order, bool>> InDatabase(AppDbContext db, Guid tenantId) => Is(db, tenantId, MicroExportState.MicroExport);

    /// <summary>The orders the rule calls domestic: the "Türkiye" region.</summary>
    internal static Expression<Func<Order, bool>> DomesticInDatabase(AppDbContext db, Guid tenantId) => Is(db, tenantId, MicroExportState.Domestic);

    /// <summary>The orders whose kind the rule cannot tell: the "Belirsiz" region.</summary>
    internal static Expression<Func<Order, bool>> UnknownInDatabase(AppDbContext db, Guid tenantId) => Is(db, tenantId, MicroExportState.Unknown);

    private static Expression<Func<Order, bool>> Is(AppDbContext db, Guid tenantId, MicroExportState state)
    {
        var ids = Ids(db, tenantId, state);
        return order => ids.Contains(order.Id);
    }
}
