using System.Collections.Concurrent;
using System.Net;
using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using WorkerService = MarketplaceHub.Worker.Worker;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-1: a write job (not cancelled by a worker shutdown) that is still waiting for its marketplace quota when the
// shutdown begins stops waiting: the request is not sent, it ends as a local quota wait and the job goes back to the queue
// without spending an attempt. A request that already has its quota token finishes as before.
public sealed class JobShutdownSignalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AWriteJobWaitingForQuotaAtShutdownIsHandedBackUnsent()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = 0;
        // The quota stays full: the rate-limit handler never hands out the token (never calls the acquired option).
        var processor = new TransportProcessor(new Handler(async (_, token) =>
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            Interlocked.Increment(ref sent);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var leases = new RecordingLeases();
        using var stopping = new CancellationTokenSource();

        var run = NewWorker(leases, processor).ExecuteLeasedJobAsync(Job(MarketplaceJobTypes.ShipmentAction), stopping.Token);
        await waiting.Task.WaitAsync(Patience);
        stopping.Cancel();
        await run.WaitAsync(Patience);

        Assert.Equal(0, sent);
        var error = Assert.Single(processor.Errors);
        Assert.Equal("N11_LOCAL_QUOTA_WAIT", error.Code);
        // What the write's effect record does with that answer: released (not applied), so nothing waits for review.
        Assert.Equal(ExternalEffectSendOutcome.Retryable, ExternalEffectGuard.Classify(error));
        Assert.Equal(1, leases.Released);
        Assert.Empty(leases.Completed);
    }

    [Fact]
    public async Task AWriteJobWhoseRequestWasSentFinishesAndRecordsItsOutcome()
    {
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledInFlight = false;
        var processor = new TransportProcessor(new Handler(async (request, token) =>
        {
            request.Options.TryGetValue(N11HttpClient.Transport.QuotaAcquiredKey, out var acquired);
            acquired!();
            inFlight.TrySetResult();
            await answer.Task;
            cancelledInFlight = token.IsCancellationRequested;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var leases = new RecordingLeases();
        using var stopping = new CancellationTokenSource();

        var run = NewWorker(leases, processor).ExecuteLeasedJobAsync(Job(MarketplaceJobTypes.ShipmentAction), stopping.Token);
        await inFlight.Task.WaitAsync(Patience);
        stopping.Cancel();
        answer.SetResult();
        await run.WaitAsync(Patience);

        Assert.False(cancelledInFlight);
        Assert.Empty(processor.Errors);
        Assert.Equal(0, leases.Released);
        Assert.Equal(JobCompletionKind.Succeeded, Assert.Single(leases.Completed).Kind);
    }

    [Fact]
    public async Task AReadJobIsStillCancelledByTheShutdownAndHandedBack()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new TransportProcessor(new Handler(async (_, token) =>
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var leases = new RecordingLeases();
        using var stopping = new CancellationTokenSource();

        var run = NewWorker(leases, processor).ExecuteLeasedJobAsync(Job(MarketplaceJobTypes.N11OrderSync), stopping.Token);
        await waiting.Task.WaitAsync(Patience);
        stopping.Cancel();
        await run.WaitAsync(Patience);

        // As before: the caller's own token was cancelled, so no adapter answer is made up and the job is released.
        Assert.Empty(processor.Errors);
        Assert.Equal(1, leases.Released);
        Assert.Empty(leases.Completed);
    }

    [Fact]
    public async Task ARequestAWriteJobStartsAfterTheShutdownIsNotSent()
    {
        var sent = 0;
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        using var scope = JobShutdownSignal.Enter(stopping.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/x");

        using var exchange = await N11HttpClient.Transport.SendAsync(new Factory(new Handler((_, _) =>
        {
            Interlocked.Increment(ref sent);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        })), request, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, sent);
        Assert.Equal("N11_LOCAL_QUOTA_WAIT", exchange.Error!.Code);
        Assert.Equal(1, scope.QuotaWaitsStopped);
    }

    [Fact]
    public async Task WithoutTheSignalAQuotaWaitIsNotTouchedByAnyShutdown()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/x");
        using var exchange = await N11HttpClient.Transport.SendAsync(new Factory(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }))), request, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(exchange.Error);
        Assert.Equal("ok", exchange.Body);
    }

    [Fact]
    public async Task TrendyolStopsWaitingForItsQuotaAtShutdownWithoutSending()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = 0;
        var client = TrendyolClient(new Handler(async (_, token) =>
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            Interlocked.Increment(ref sent);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var stopping = new CancellationTokenSource();
        using var scope = JobShutdownSignal.Enter(stopping.Token);
        var end = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        var scan = client.ScanPackageStreamAsync(TrendyolAccount(), "pkg-1", null, null, end.AddDays(-12), end, "TR", CancellationToken.None);
        await waiting.Task.WaitAsync(Patience);
        stopping.Cancel();
        var result = await scan.WaitAsync(Patience);

        Assert.Equal(0, sent);
        Assert.Equal(TrendyolHttpClient.LocalQuotaWaitCode, result.StreamError!.Code);
        Assert.Equal(AdapterErrorClass.RateLimit, result.StreamError.Class);
        Assert.Equal(1, scope.QuotaWaitsStopped);
    }

    [Fact]
    public async Task TrendyolFinishesARequestThatHasItsQuotaTokenDespiteTheShutdown()
    {
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledInFlight = false;
        var client = TrendyolClient(new Handler(async (request, token) =>
        {
            request.Options.TryGetValue(TrendyolResilienceHandler.QuotaAcquiredKey, out var acquired);
            acquired!();
            inFlight.TrySetResult();
            await answer.Task;
            cancelledInFlight = token.IsCancellationRequested;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[{"id":"pkg-1","orderNumber":"ORD-1","orderDate":1760000000000,"currencyCode":"TRY","status":"Created","lastModifiedDate":1760000000000,"lines":[]}],"hasMore":false}""", Encoding.UTF8, "application/json")
            };
        }));
        using var stopping = new CancellationTokenSource();
        using var scope = JobShutdownSignal.Enter(stopping.Token);
        var end = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        var scan = client.ScanPackageStreamAsync(TrendyolAccount(), "pkg-1", null, null, end.AddDays(-12), end, "TR", CancellationToken.None);
        await inFlight.Task.WaitAsync(Patience);
        stopping.Cancel();
        answer.SetResult();
        var result = await scan.WaitAsync(Patience);

        Assert.False(cancelledInFlight);
        Assert.Null(result.StreamError);
        Assert.True(result.Result!.IsSuccess);
        Assert.Equal(0, scope.QuotaWaitsStopped);
    }

    [Fact]
    public void AWriteJobGoesBackToTheQueueOnlyWhenTheShutdownStoppedAQuotaWaitAndItEndsToBeRetried()
    {
        var quotaWait = JobExecutionResult.FromAdapterError(new AdapterError(AdapterErrorClass.RateLimit, "LOCAL_QUOTA_WAIT", "x", 429, TimeSpan.FromSeconds(15), null));
        Assert.True(WorkerService.ReleasesOnShutdown(leaseHeld: true, cancelledByShutdown: false, quotaWaitStoppedByShutdown: true, quotaWait));
        // Its other outcomes are recorded as they are, and so is any outcome when the shutdown stopped nothing.
        Assert.False(WorkerService.ReleasesOnShutdown(true, false, true, JobExecutionResult.Success()));
        Assert.False(WorkerService.ReleasesOnShutdown(true, false, true, JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS")));
        Assert.False(WorkerService.ReleasesOnShutdown(true, false, true, null));
        Assert.False(WorkerService.ReleasesOnShutdown(true, false, false, quotaWait));
        Assert.False(WorkerService.ReleasesOnShutdown(false, false, true, quotaWait));
        // A job the shutdown cancelled goes back as before.
        Assert.True(WorkerService.ReleasesOnShutdown(true, true, false, null));
        Assert.True(WorkerService.ReleasesOnShutdown(true, true, false, JobExecutionResult.Retry("TRANSIENT_EXECUTION_FAILURE")));
        Assert.False(WorkerService.ReleasesOnShutdown(true, true, false, JobExecutionResult.Success()));
    }

    private static WorkerService NewWorker(RecordingLeases leases, TransportProcessor processor)
    {
        var services = new ServiceCollection()
            .AddSingleton<IJobLeaseService>(leases)
            .AddSingleton<IMarketplaceJobProcessor>(processor)
            .BuildServiceProvider();
        return new WorkerService(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkerService>.Instance, new ConfigurationBuilder().Build(), null!);
    }

    private static LeasedJob Job(string jobType) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), jobType, "{}", "test", "lease-token");

    private static TrendyolHttpClient TrendyolClient(HttpMessageHandler handler) =>
        new(new Factory(handler), null!, new ConfigurationBuilder().Build(), Options.Create(new TrendyolOptions()), TimeProvider.System, NullLogger<TrendyolHttpClient>.Instance);

    private static TrendyolRequestContext TrendyolAccount() => new(
        new PlatformConnection { PlatformCode = "TRENDYOL", Environment = "PRODUCTION", Status = "ACTIVE", DisplayName = "Trendyol", ExternalStoreId = "seller-1", ApiVersion = "V2" },
        new Uri("https://apigw.example/"), "key", "secret", "seller-1 - SelfIntegration", false);

    // A processor whose job is one n11 request through the shared quota-paced send step.
    private sealed class TransportProcessor(HttpMessageHandler handler) : IMarketplaceJobProcessor
    {
        public ConcurrentQueue<AdapterError> Errors { get; } = new();

        public async Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken, Guid? jobId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/x");
            using var exchange = await N11HttpClient.Transport.SendAsync(new Factory(handler), request, TimeSpan.FromSeconds(5), cancellationToken);
            if (exchange.Error is not { } error) return JobExecutionResult.Success();
            Errors.Enqueue(error);
            return JobExecutionResult.FromAdapterError(error);
        }
    }

    private sealed class RecordingLeases : IJobLeaseService
    {
        private int released;
        public int Released => released;
        public ConcurrentQueue<JobExecutionResult> Completed { get; } = new();

        public Task<LeasedJob?> TryLeaseAsync(TimeSpan leaseDuration, int? maximumPriority, int? minimumPriority, CancellationToken cancellationToken, string? onlyJobType = null, Guid? onlyJobId = null, string[]? includedJobTypes = null, string[]? excludedJobTypes = null) =>
            Task.FromResult<LeasedJob?>(null);
        public Task<bool> HeartbeatAsync(Guid jobId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> CompleteAsync(Guid jobId, string leaseToken, JobExecutionResult result, CancellationToken cancellationToken)
        {
            Completed.Enqueue(result);
            return Task.FromResult(true);
        }
        public Task<int> ReapExpiredAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<bool> ReleaseAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref released);
            return Task.FromResult(true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request, cancellationToken);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
