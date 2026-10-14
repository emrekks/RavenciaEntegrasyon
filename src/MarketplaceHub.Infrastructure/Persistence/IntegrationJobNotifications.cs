namespace MarketplaceHub.Infrastructure.Persistence;

public static class IntegrationJobNotifications
{
    // Raised by a trigger on integration.jobs when a job becomes runnable now, or when a leased
    // job finishes and may unblock a job waiting for the same connection lock group.
    public const string Channel = "integration_jobs_runnable";
}
