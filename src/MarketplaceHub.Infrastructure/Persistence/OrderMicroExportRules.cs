using System.Data;
using System.Linq.Expressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// The one micro-export rule (MicroExportRule; owner's decision of 27 Nov 2026, finding C-11.9) as database conditions on
/// an order. The order list's Mikro İhracat and Fatura Bölgesi filters and the micro-export card read these; the label,
/// the row flag and the invoice read MicroExportRule.Classify in memory. Both are the same rule (MicroExportRuleTests run
/// every case through both engines). The order's marketplace comes from its connection.
/// </summary>
internal static class OrderMicroExportRules
{
    private static readonly RelationalTypeMapping TextMapping = new StringTypeMapping("text", DbType.String);
    private static readonly RelationalTypeMapping JsonbMapping = new StringTypeMapping("jsonb", DbType.String);
    private static readonly RelationalTypeMapping BoolMapping = new BoolTypeMapping("boolean");
    private static readonly RelationalTypeMapping IntMapping = new IntTypeMapping("integer");

    /// <summary>
    /// Maps the rule's two helpers to PostgreSQL: TopLevelJson(json, key) is
    /// <c>CASE WHEN pg_input_is_valid(json, 'jsonb') THEN jsonb_extract_path(json::jsonb, key)::text END</c> (a text that
    /// is no JSON reads as missing, as JsonDocument does in memory, and never fails the query); SignalKeyCount(json) is
    /// <c>regexp_count(json, SignalKeyPattern)</c>.
    /// </summary>
    internal static void MapFunctions(ModelBuilder builder)
    {
        builder.HasDbFunction(typeof(MicroExportRule).GetMethod(nameof(MicroExportRule.TopLevelJson), [typeof(string), typeof(string)])!)
            .HasTranslation(arguments =>
            {
                var json = Text(arguments[0]);
                var key = Text(arguments[1]);
                var valid = new SqlFunctionExpression("pg_input_is_valid", [json, new SqlConstantExpression("jsonb", typeof(string), TextMapping)],
                    nullable: true, argumentsPropagateNullability: [true, false], typeof(bool), BoolMapping);
                var value = new SqlUnaryExpression(ExpressionType.Convert,
                    new SqlFunctionExpression("jsonb_extract_path", [new SqlUnaryExpression(ExpressionType.Convert, json, typeof(string), JsonbMapping), key],
                        nullable: true, argumentsPropagateNullability: [true, true], typeof(string), JsonbMapping),
                    typeof(string), TextMapping);
                return new CaseExpression([new CaseWhenClause(valid, value)], elseResult: null);
            });
        builder.HasDbFunction(typeof(MicroExportRule).GetMethod(nameof(MicroExportRule.SignalKeyCount), [typeof(string)])!)
            .HasTranslation(arguments => new SqlFunctionExpression("regexp_count",
                [Text(arguments[0]), new SqlConstantExpression(MicroExportRule.SignalKeyPattern, typeof(string), TextMapping)],
                nullable: true, argumentsPropagateNullability: [true, false], typeof(int), IntMapping));
    }

    private static SqlExpression Text(SqlExpression expression) => expression switch
    {
        { TypeMapping: not null } => expression,
        SqlConstantExpression constant => new SqlConstantExpression(constant.Value, typeof(string), TextMapping),
        _ => expression
    };

    /// <summary>The rule's answer for an order, as the database runs it.</summary>
    internal static Expression<Func<Order, MicroExportState>> State(AppDbContext db)
    {
        Expression<Func<Order, string?>> platform = order => db.PlatformConnections.Where(connection => connection.Id == order.ConnectionId)
            .Select(connection => connection.PlatformCode).FirstOrDefault();
        Expression<Func<Order, string?>> storefront = order => order.StoreFrontCode;
        Expression<Func<Order, string?>> json = order => order.CustomerSnapshotJson;
        var row = Expression.Parameter(typeof(Order), "order");
        var arguments = new[] { platform, storefront, json }
            .Select(field => Microsoft.EntityFrameworkCore.Query.ReplacingExpressionVisitor.Replace(field.Parameters[0], row, field.Body)).ToArray();
        return Expression.Lambda<Func<Order, MicroExportState>>(PolicyExpressions.Inline(MicroExportRule.StateRule, arguments), row);
    }

    /// <summary>The orders the rule calls micro export: the "Mikro İhracat" filter and card.</summary>
    internal static Expression<Func<Order, bool>> InDatabase(AppDbContext db) => Is(db, MicroExportState.MicroExport);

    /// <summary>The orders the rule calls domestic: the "Türkiye" invoice region. An Unknown order is in neither region.</summary>
    internal static Expression<Func<Order, bool>> DomesticInDatabase(AppDbContext db) => Is(db, MicroExportState.Domestic);

    private static Expression<Func<Order, bool>> Is(AppDbContext db, MicroExportState state)
    {
        var rule = State(db);
        return Expression.Lambda<Func<Order, bool>>(Expression.Equal(rule.Body, Expression.Constant(state)), rule.Parameters);
    }
}
