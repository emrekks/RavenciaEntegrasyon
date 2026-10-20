using MarketplaceHub.Api.Security;

namespace MarketplaceHub.Api.Operations;

// Work the panel runs inside a request (invoices cut at once, a picking action, a connection test, any
// other instant marketplace write) is bounded by the server, never by the browser: a closed tab or a
// dropped connection must not cancel an e-Archive invoice or a marketplace write midway and leave its
// job leased. No new item or step starts after the 60-second budget; a step already started may finish
// within a 30-second grace, and only a server shutdown or the end of that grace cancels it. At most five
// items run in one request; the rest stay queued for the worker and are answered with their job ids.
public sealed class InlineExecutionBudget : IDisposable
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    public const int MaximumInlineItems = 5;
    private const string ItemKey = "MarketplaceHub.InlineExecutionBudget";

    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource hardStop;
    private readonly CancellationTokenSource linked;

    public InlineExecutionBudget(IHostApplicationLifetime lifetime, TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        Deadline = timeProvider.GetUtcNow().Add(Budget);
        hardStop = new CancellationTokenSource(Budget + Grace, timeProvider);
        linked = CancellationTokenSource.CreateLinkedTokenSource(hardStop.Token, lifetime.ApplicationStopping);
    }

    public DateTimeOffset Deadline { get; }

    public CancellationToken Token => linked.Token;

    public bool HasTimeLeft => !linked.IsCancellationRequested && timeProvider.GetUtcNow() < Deadline;

    // The budget of the running request, when its endpoint uses WithServerBudget().
    public static InlineExecutionBudget? Current(HttpContext http) => http.Items[ItemKey] as InlineExecutionBudget;

    // Runs the runnable items in order while the budget lasts, at most MaximumInlineItems of them; every
    // other item is returned as it came. A run cut short by shutdown or the hard stop keeps its item as it
    // came (still queued) and starts nothing more.
    public async Task<IReadOnlyList<T>> RunAsync<T>(IReadOnlyList<T> items, Func<T, bool> runnable, Func<T, CancellationToken, Task<T>> run)
    {
        var results = new List<T>(items.Count);
        var started = 0;
        foreach (var item in items)
        {
            if (!runnable(item) || started >= MaximumInlineItems || !HasTimeLeft)
            {
                results.Add(item);
                continue;
            }
            started++;
            try
            {
                results.Add(await run(item, Token));
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                results.Add(item);
            }
        }
        return results;
    }

    // Endpoint filter: while the endpoint runs, http.RequestAborted is the budget's token instead of the
    // client's, so every call the endpoint makes with it is bounded by the server.
    internal static async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        using var budget = new InlineExecutionBudget(http.RequestServices.GetRequiredService<IHostApplicationLifetime>(), http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
        var clientToken = http.RequestAborted;
        http.RequestAborted = budget.Token;
        http.Items[ItemKey] = budget;
        try
        {
            return await next(context);
        }
        catch (OperationCanceledException) when (budget.Token.IsCancellationRequested)
        {
            return ApiProblems.Of(http, "INLINE_BUDGET_EXHAUSTED", "İşlem sunucunun süre sınırında tamamlanamadı; sonucu iş listesinden kontrol edin.", StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            http.Items.Remove(ItemKey);
            http.RequestAborted = clientToken;
        }
    }

    public void Dispose()
    {
        linked.Dispose();
        hardStop.Dispose();
    }
}

public static class InlineExecutionBudgetExtensions
{
    public static TBuilder WithServerBudget<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(InlineExecutionBudget.FilterAsync);
}
