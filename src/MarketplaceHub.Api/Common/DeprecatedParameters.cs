using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MarketplaceHub.Api.Security;

namespace MarketplaceHub.Api.Common;

// API-GUVENLIK-8, the removal one step later: the old query parameters (FilterContract's renamed filters and the paging
// formats' deprecated parameters, PagingContract) are still accepted beside the unified ones, because a browser tab
// opened before the web switched still sends them. One switch decides it for every endpoint, read here for each request:
// Api:AcceptDeprecatedParameters (default true).
//   true:  the request goes on as before (EndpointHelpers.Unified and the paging helpers read the old name), and a use is
//          counted (marketplacehub.api.deprecated_parameters, tagged by route and parameter) and logged once per route
//          and parameter per hour: the name and its replacement only, never a query value or anything about the caller.
//          When the log stays quiet for long enough, nothing sends them any more.
//   false: an old name with a value is refused, 400 DEPRECATED_PARAMETER naming the parameter to send instead
//          (`replacement`); the unified names answer as before.
// ApiDeprecatedParametersTests holds this table equal to the parameters the document marks deprecated.
public static class DeprecatedParameters
{
    public const string SettingKey = "Api:AcceptDeprecatedParameters";
    public const string RefusedCode = "DEPRECATED_PARAMETER";

    internal static readonly TimeSpan LogInterval = TimeSpan.FromHours(1);

    // The paging formats' deprecated parameters and what replaces each: the cursor (after) for a page number, limit
    // for a page size.
    private static readonly Dictionary<string, string> PagingReplacement = new(StringComparer.Ordinal)
    {
        ["page"] = "after",
        ["pageNumber"] = "after",
        ["pageSize"] = "limit",
    };

    // Route (as the document names it; none of these has a route parameter) → old name → the old parameter. Routing and
    // query binding ignore case, so the lookups do too; the entries keep the document's spelling.
    private static readonly Dictionary<string, Dictionary<string, DeprecatedParameter>> Table = Build();

    /// <summary>Every old parameter still accepted, with its replacement.</summary>
    public static IEnumerable<DeprecatedParameter> All => Table.Values.SelectMany(names => names.Values);

    private static Dictionary<string, Dictionary<string, DeprecatedParameter>> Build()
    {
        var table = new Dictionary<string, Dictionary<string, DeprecatedParameter>>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string old, string replacement)
        {
            if (!table.TryGetValue(path, out var names)) table[path] = names = new(StringComparer.OrdinalIgnoreCase);
            names[old] = new(path, old, replacement);
        }
        foreach (var (path, renamed) in FilterContract.RenamedFilters)
            foreach (var (old, replacement) in renamed) Add(path, old, replacement);
        foreach (var (path, format) in PagingContract.PagedLists)
            foreach (var old in format.Deprecated ?? new HashSet<string>())
                Add(path, old, PagingReplacement.TryGetValue(old, out var replacement)
                    ? replacement
                    : throw new InvalidOperationException($"No replacement for the deprecated paging parameter {old} of {path}."));
        return table;
    }

    /// <summary>The old parameters a GET request sends with a value (empty for most requests).</summary>
    public static IReadOnlyList<DeprecatedParameter> Used(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) || request.Query.Count == 0) return [];
        var path = request.Path.Value?.TrimEnd('/');
        if (string.IsNullOrEmpty(path) || !Table.TryGetValue(path, out var names)) return [];
        List<DeprecatedParameter>? used = null;
        foreach (var (key, value) in request.Query)
            if (!string.IsNullOrEmpty(value.ToString()) && names.TryGetValue(key, out var parameter)) (used ??= []).Add(parameter);
        return used ?? [];
    }
}

/// <summary>An old query parameter of a GET route and the unified one to send instead.</summary>
public sealed record DeprecatedParameter(string Route, string Parameter, string Replacement);

public sealed class DeprecatedParameterMiddleware(RequestDelegate next, IConfiguration configuration, TimeProvider timeProvider, ILogger<DeprecatedParameterMiddleware> logger)
{
    private static readonly Meter Meter = new("MarketplaceHub.Api");
    private static readonly Counter<long> Uses = Meter.CreateCounter<long>(
        "marketplacehub.api.deprecated_parameters", description: "Requests that named an old query parameter, by route and parameter.");

    // Route + parameter → when it was last logged.
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastLogged = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        var used = DeprecatedParameters.Used(context.Request);
        if (used.Count == 0)
        {
            await next(context);
            return;
        }

        if (!configuration.GetValue(DeprecatedParameters.SettingKey, true))
        {
            var (_, parameter, replacement) = used[0];
            // The code is the same for every old name; the answer names the one to send instead.
            await ApiProblems.WriteAsync(context, DeprecatedParameters.RefusedCode,
                $"{parameter} parametresi artık kabul edilmiyor; yerine {replacement} gönderin.", StatusCodes.Status400BadRequest,
                new Dictionary<string, object?> { ["parameter"] = parameter, ["replacement"] = replacement });
            return;
        }

        var now = timeProvider.GetUtcNow();
        foreach (var (route, parameter, replacement) in used)
        {
            Uses.Add(1, new KeyValuePair<string, object?>("route", route), new KeyValuePair<string, object?>("parameter", parameter));
            var key = route + " " + parameter;
            if (lastLogged.TryGetValue(key, out var previous))
            {
                if (now - previous < DeprecatedParameters.LogInterval || !lastLogged.TryUpdate(key, now, previous)) continue;
            }
            else if (!lastLogged.TryAdd(key, now)) continue;
            logger.LogInformation("Deprecated query parameter {Parameter} used on GET {Route}; replacement {Replacement} (logged at most once an hour per route and parameter)",
                parameter, route, replacement);
        }
        await next(context);
    }
}
