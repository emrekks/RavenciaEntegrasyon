using MarketplaceHub.Api.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InlineExecutionBudgetTests
{
    [Fact]
    public async Task AtMostFiveItemsRunInline_TheRestStayQueuedWithTheirJobs()
    {
        using var budget = new InlineExecutionBudget(new Lifetime(), new ManualTime());
        var items = Enumerable.Range(0, 8).Select(index => new Item(index, "QUEUED")).Append(new Item(8, "SKIPPED")).ToList();

        var results = await budget.RunAsync(items, item => item.Status == "QUEUED", (item, _) => Task.FromResult(item with { Status = "COMPLETED" }));

        Assert.Equal(9, results.Count);
        Assert.Equal(5, results.Count(item => item.Status == "COMPLETED"));
        Assert.All(results.Take(5), item => Assert.Equal("COMPLETED", item.Status));
        Assert.All(results.Skip(5).Take(3), item => Assert.Equal("QUEUED", item.Status));
        Assert.Equal("SKIPPED", results[^1].Status);
    }

    [Fact]
    public async Task NoItemStartsAfterTheBudgetRanOut()
    {
        var time = new ManualTime();
        using var budget = new InlineExecutionBudget(new Lifetime(), time);
        var items = new[] { new Item(0, "QUEUED"), new Item(1, "QUEUED") };

        var results = await budget.RunAsync(items, _ => true, (item, _) =>
        {
            time.Now += InlineExecutionBudget.Budget;
            return Task.FromResult(item with { Status = "COMPLETED" });
        });

        Assert.Equal(["COMPLETED", "QUEUED"], results.Select(item => item.Status));
    }

    [Fact]
    public async Task ServerShutdownStopsTheRun_TheInterruptedItemStaysQueued()
    {
        var lifetime = new Lifetime();
        using var budget = new InlineExecutionBudget(lifetime, new ManualTime());
        var items = new[] { new Item(0, "QUEUED"), new Item(1, "QUEUED") };

        var results = await budget.RunAsync(items, _ => true, async (item, token) =>
        {
            await lifetime.StopAsync();
            token.ThrowIfCancellationRequested();
            return item with { Status = "COMPLETED" };
        });

        Assert.Equal(["QUEUED", "QUEUED"], results.Select(item => item.Status));
    }

    [Fact]
    public async Task Filter_ReplacesTheClientsTokenWhileTheEndpointRuns()
    {
        using var client = new CancellationTokenSource();
        var http = NewContext(new Lifetime());
        http.RequestAborted = client.Token;
        var tokenSeen = CancellationToken.None;
        InlineExecutionBudget? budgetSeen = null;

        await InlineExecutionBudget.FilterAsync(EndpointFilterInvocationContext.Create(http), context =>
        {
            tokenSeen = context.HttpContext.RequestAborted;
            budgetSeen = InlineExecutionBudget.Current(context.HttpContext);
            // The browser goes away mid-write: the server's token is not cancelled.
            client.Cancel();
            return ValueTask.FromResult<object?>(Results.Ok());
        });

        Assert.NotEqual(client.Token, tokenSeen);
        Assert.False(tokenSeen.IsCancellationRequested);
        Assert.NotNull(budgetSeen);
        Assert.Equal(client.Token, http.RequestAborted);
        Assert.Null(InlineExecutionBudget.Current(http));
    }

    [Fact]
    public async Task Filter_AnswersAShutdownWithServiceUnavailable()
    {
        var lifetime = new Lifetime();
        var http = NewContext(lifetime);

        var result = await InlineExecutionBudget.FilterAsync(EndpointFilterInvocationContext.Create(http), async context =>
        {
            await lifetime.StopAsync();
            context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            return Results.Ok();
        });

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    private static DefaultHttpContext NewContext(Lifetime lifetime) => new()
    {
        RequestServices = new ServiceCollection().AddSingleton<IHostApplicationLifetime>(lifetime).AddSingleton<TimeProvider>(new ManualTime()).BuildServiceProvider()
    };

    private sealed record Item(int Index, string Status);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
        public Task StopAsync() => stopping.CancelAsync();
    }
}
