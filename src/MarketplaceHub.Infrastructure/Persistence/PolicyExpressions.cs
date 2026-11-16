using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-10: a rule written once as an expression over its own inputs (ListingSaleState.ClosedBeyondStockRule,
// ImportedApprovalStates.IsHeldListingRule, ...) is applied to a row by putting the row's fields in for the rule's
// parameters. EF then translates the rule itself, and the in-memory callers compile the same expression: no second
// copy of the rule is written for the database.
internal static class PolicyExpressions
{
    public static Expression<Func<TRow, bool>> On<TRow, T1>(Expression<Func<T1, bool>> rule, Expression<Func<TRow, T1>> first) =>
        Bind<TRow>(rule, first);

    public static Expression<Func<TRow, bool>> On<TRow, T1, T2>(Expression<Func<T1, T2, bool>> rule, Expression<Func<TRow, T1>> first, Expression<Func<TRow, T2>> second) =>
        Bind<TRow>(rule, first, second);

    public static Expression<Func<TRow, bool>> On<TRow, T1, T2, T3>(Expression<Func<T1, T2, T3, bool>> rule, Expression<Func<TRow, T1>> first, Expression<Func<TRow, T2>> second, Expression<Func<TRow, T3>> third) =>
        Bind<TRow>(rule, first, second, third);

    // The rule's body with each of its parameters replaced by the matching argument's body over one row parameter.
    public static Expression Inline(LambdaExpression rule, params Expression[] arguments)
    {
        if (rule.Parameters.Count != arguments.Length) throw new ArgumentException("Every parameter of the rule needs an argument.", nameof(arguments));
        var body = rule.Body;
        for (var index = 0; index < arguments.Length; index++)
            body = ReplacingExpressionVisitor.Replace(rule.Parameters[index], arguments[index], body);
        return body;
    }

    private static Expression<Func<TRow, bool>> Bind<TRow>(LambdaExpression rule, params LambdaExpression[] fields)
    {
        var row = Expression.Parameter(typeof(TRow), "row");
        var arguments = fields.Select(field => ReplacingExpressionVisitor.Replace(field.Parameters[0], row, field.Body)).ToArray();
        return Expression.Lambda<Func<TRow, bool>>(Inline(rule, arguments), row);
    }
}
