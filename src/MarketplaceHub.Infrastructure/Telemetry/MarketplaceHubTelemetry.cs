using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MarketplaceHub.Infrastructure.Telemetry;

// Metrics and traces with the platform's own APIs: readable with dotnet-counters / dotnet-trace in a
// container today, and by an OpenTelemetry exporter once one is added (meter and source "MarketplaceHub").
public static class MarketplaceHubTelemetry
{
    public const string Name = "MarketplaceHub";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>(
        "marketplacehub.jobs.completed", unit: "{job}", description: "Jobs finished, by job type and outcome.");
    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "marketplacehub.jobs.duration", unit: "s", description: "How long a job ran, by job type and outcome.");
    public static readonly Counter<long> MarketplaceRequests = Meter.CreateCounter<long>(
        "marketplacehub.marketplace.requests", unit: "{request}", description: "Requests sent to a marketplace, by client and status.");
    public static readonly Histogram<double> MarketplaceRequestDuration = Meter.CreateHistogram<double>(
        "marketplacehub.marketplace.request.duration", unit: "s", description: "Round trip of a marketplace request (after quota waits), by client and status.");
}

// Innermost handler of each marketplace client: counts the requests that really leave the server and
// times their round trip; local circuit-breaker answers and quota waits are outside it.
public sealed class MarketplaceRequestMetricsHandler(string client) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        string status = "error";
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            status = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return response;
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            throw;
        }
        finally
        {
            var tags = new TagList { { "client", client }, { "method", request.Method.Method }, { "status", status } };
            MarketplaceHubTelemetry.MarketplaceRequests.Add(1, tags);
            MarketplaceHubTelemetry.MarketplaceRequestDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }
}
