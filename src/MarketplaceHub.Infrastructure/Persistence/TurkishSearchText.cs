using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A text search that ignores case the Turkish way whatever the database locale (O3, 20 Oct 2026).
// The live database is en_US: lower('I') is 'i', never 'ı', so ILIKE alone never found "Işıklı"
// for "ışık"; a C-locale database does not fold "Ş" to "ş" either. Both sides are folded the same
// way before ILIKE: the four i letters (İ I ı i) become "i" and the other Turkish capitals (Ş Ğ Ü
// Ö Ç) their small letter; ILIKE then folds the ASCII letters.
internal static class TurkishSearchText
{
    private static readonly (string From, string To)[] Map =
        [("İ", "i"), ("I", "i"), ("ı", "i"), ("Ş", "ş"), ("Ğ", "ğ"), ("Ü", "ü"), ("Ö", "ö"), ("Ç", "ç")];

    private static readonly MethodInfo Replace = typeof(string).GetMethod(nameof(string.Replace), [typeof(string), typeof(string)])!;

    private static readonly MethodInfo ILike = typeof(NpgsqlDbFunctionsExtensions).GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    // The term folded like the column (Fold in SQL).
    public static string Fold(string value)
    {
        foreach (var (from, to) in Map) value = value.Replace(from, to, StringComparison.Ordinal);
        return value;
    }

    // value => ILIKE(fold(selector(value)), pattern, escape), with the pattern sent as a parameter.
    public static Expression<Func<T, bool>> FoldedILike<T>(Expression<Func<T, string?>> selector, string pattern, string escape)
    {
        Expression folded = selector.Body;
        foreach (var (from, to) in Map) folded = Expression.Call(folded, Replace, Expression.Constant(from), Expression.Constant(to));
        var call = Expression.Call(ILike, Expression.Constant(EF.Functions), folded,
            Expression.Property(Expression.Constant(new PatternHolder(pattern)), nameof(PatternHolder.Pattern)),
            Expression.Constant(escape));
        return Expression.Lambda<Func<T, bool>>(call, selector.Parameters);
    }

    private sealed record PatternHolder(string Pattern);
}
