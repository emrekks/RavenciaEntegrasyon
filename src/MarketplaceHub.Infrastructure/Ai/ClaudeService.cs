using System.Diagnostics;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Ai;

// Who asks Claude for what: logged with every call (Ayarlar › Yapay Zekâ › Kullanım Kaydı).
public sealed record AiCallContext(Guid TenantId, Guid? UserId, string Feature, string? SubjectType = null, Guid? SubjectId = null, string? SubjectLabel = null);

public sealed record AiSkillRun(ClaudeSkillResult Result, Guid LogId, string Model);

public sealed record AiUsageLogView(Guid Id, DateTimeOffset CreatedAt, string Feature, string FeatureLabel, string? UserName, string? SubjectLabel, string Model, long? InputTokens, long? OutputTokens, decimal? CostUsd, string Status, string? Error, int DurationMs);

// List prices per million tokens (Claude API, 2026-10): input, output, cache read, cache write (5 minutes).
public static class ClaudePricing
{
    private static readonly Dictionary<string, (decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)> PerMillion = new(StringComparer.Ordinal)
    {
        ["claude-sonnet-5-5"] = (2m, 10m, 0.20m, 2.50m),
        ["claude-sonnet-5"] = (2m, 10m, 0.20m, 2.50m),
        ["claude-opus-5-5"] = (4m, 20m, 0.20m, 5m),
        ["claude-opus-5"] = (5m, 25m, 0.50m, 6.25m),
        ["claude-haiku-5-5"] = (0.10m, 0.50m, 0.01m, 0.125m),
        ["claude-fable-5-1"] = (10m, 50m, 0.25m, 12.50m),
    };

    // Null when the model's price is unknown or the API reported no usage. Code-execution container time is billed
    // separately by Anthropic and is not in this figure.
    public static decimal? CostUsd(string model, ClaudeUsage usage)
    {
        if (!PerMillion.TryGetValue(model, out var price) || usage.InputTokens is null && usage.OutputTokens is null) return null;
        var cost = (usage.InputTokens ?? 0) * price.Input + (usage.OutputTokens ?? 0) * price.Output
            + (usage.CacheReadInputTokens ?? 0) * price.CacheRead + (usage.CacheCreationInputTokens ?? 0) * price.CacheWrite;
        return Math.Round(cost / 1_000_000m, 6);
    }
}

// The shared service every AI feature calls: it loads the key and model from the settings, calls Claude through
// IClaudeClient and writes one usage row per call (feature, who, subject, tokens, cost, outcome). The size chart is
// its first consumer; later features reuse it with their own feature name.
public sealed class ClaudeService(AiSettingsService settings, IClaudeClient client, AppDbContext db, TimeProvider timeProvider)
{
    public static readonly IReadOnlyDictionary<string, string> FeatureLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CONNECTION_TEST"] = "Bağlantı Sınaması",
        ["SKILL_UPLOAD"] = "Beceri Yükleme",
        ["SIZE_CHART"] = "Beden Tablosu",
    };

    public async Task<AiCredentials> RequireCredentialsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await settings.LoadCredentialsAsync(tenantId, cancellationToken)
        ?? throw new ClaudeException("AI_NOT_CONFIGURED", "Claude API anahtarı kayıtlı değil. Ayarlar › Yapay Zekâ bölümünden girin.", 409);

    public Task<AiCredentials?> CredentialsOrNullAsync(Guid tenantId, CancellationToken cancellationToken) => settings.LoadCredentialsAsync(tenantId, cancellationToken);

    public async Task<string> TestAsync(AiCallContext context, CancellationToken cancellationToken)
    {
        var credentials = await RequireCredentialsAsync(context.TenantId, cancellationToken);
        await Logged(context, credentials.DefaultModel, () => client.TestAsync(credentials.ApiKey, credentials.DefaultModel, cancellationToken), usage => usage, cancellationToken);
        return credentials.DefaultModel;
    }

    public async Task<AiSettingsView> UploadSkillAsync(AiCallContext context, string correlationId, AiSkillDefinition skill, CancellationToken cancellationToken)
    {
        var credentials = await RequireCredentialsAsync(context.TenantId, cancellationToken);
        var upload = await Logged(context, "skills-api", () => client.UploadSkillAsync(credentials.ApiKey, $"Ravencia {skill.Title}", skill.Files, cancellationToken), _ => null, cancellationToken);
        return await settings.RecordSkillAsync(context.TenantId, context.UserId ?? Guid.Empty, correlationId, skill, upload.Value, cancellationToken);
    }

    public async Task<AiSkillRun> RunSkillAsync(AiCallContext context, AiCredentials credentials, string skillKey, string system, string prompt, string effort, int maxTokens, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!credentials.Skills.TryGetValue(skillKey, out var skill))
            throw new ClaudeException("AI_SKILL_MISSING", "Beceri henüz yüklenmedi. Ayarlar › Yapay Zekâ bölümünde \"Beceriyi Yükle\" düğmesine basın.", 409);
        var model = credentials.DefaultModel;
        var run = await Logged(context, model,
            () => client.RunSkillAsync(new ClaudeSkillRequest(credentials.ApiKey, model, skill.SkillId, skill.Version, system, prompt, effort, maxTokens, timeout), cancellationToken),
            result => result.Usage, cancellationToken);
        return new(run.Value, run.LogId, model);
    }

    public Task<byte[]> DownloadFileAsync(AiCredentials credentials, string fileId, long maxBytes, CancellationToken cancellationToken) =>
        client.DownloadFileAsync(credentials.ApiKey, fileId, maxBytes, cancellationToken);

    // A call that succeeded at the API but whose result the feature refused (no image, wrong numbers): the row says so.
    public async Task MarkFailedAsync(Guid logId, string error, CancellationToken cancellationToken)
    {
        var row = await db.AiUsageLogs.SingleOrDefaultAsync(x => x.Id == logId, cancellationToken);
        if (row is null) return;
        row.Status = "FAILED";
        row.Error = Short(error);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiUsageLogView>> RecentAsync(Guid tenantId, int limit, CancellationToken cancellationToken)
    {
        var rows = await (from log in db.AiUsageLogs.AsNoTracking()
                          join user in db.Users.AsNoTracking() on log.UserId equals user.Id into users
                          from user in users.DefaultIfEmpty()
                          where log.TenantId == tenantId
                          orderby log.CreatedAt descending, log.Id descending
                          select new { log, UserName = user == null ? null : user.DisplayName }).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
        return rows.Select(row => new AiUsageLogView(row.log.Id, row.log.CreatedAt, row.log.Feature, FeatureLabels.GetValueOrDefault(row.log.Feature, row.log.Feature), row.UserName, row.log.SubjectLabel, row.log.Model, row.log.InputTokens, row.log.OutputTokens, row.log.CostUsd, row.log.Status, row.log.Error, row.log.DurationMs)).ToList();
    }

    private async Task<(T Value, Guid LogId)> Logged<T>(AiCallContext context, string model, Func<Task<T>> call, Func<T, ClaudeUsage?> usageOf, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var value = await call();
            var id = await LogAsync(context, model, usageOf(value), "SUCCEEDED", null, watch.ElapsedMilliseconds, cancellationToken);
            return (value, id);
        }
        catch (ClaudeException exception)
        {
            await LogAsync(context, model, exception.Usage, "FAILED", exception.Message, watch.ElapsedMilliseconds, CancellationToken.None);
            throw;
        }
    }

    private async Task<Guid> LogAsync(AiCallContext context, string model, ClaudeUsage? usage, string status, string? error, long elapsedMs, CancellationToken cancellationToken)
    {
        var row = new AiUsageLog
        {
            Id = Guid.CreateVersion7(),
            TenantId = context.TenantId,
            UserId = context.UserId,
            Feature = context.Feature,
            SubjectType = context.SubjectType,
            SubjectId = context.SubjectId,
            SubjectLabel = context.SubjectLabel is { Length: > 200 } label ? label[..200] : context.SubjectLabel,
            Model = model.Length <= 80 ? model : model[..80],
            InputTokens = usage?.InputTokens,
            OutputTokens = usage?.OutputTokens,
            CacheReadInputTokens = usage?.CacheReadInputTokens,
            CacheCreationInputTokens = usage?.CacheCreationInputTokens,
            CostUsd = usage is null ? null : ClaudePricing.CostUsd(model, usage),
            Status = status,
            Error = error is null ? null : Short(error),
            DurationMs = (int)Math.Min(elapsedMs, int.MaxValue),
            CreatedAt = timeProvider.GetUtcNow()
        };
        db.AiUsageLogs.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    private static string Short(string value) => value.Length <= 500 ? value : value[..500];
}
