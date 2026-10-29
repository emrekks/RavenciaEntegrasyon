using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The link verification (LISTING_VERIFY) and the single-code lookups (LISTING_LOOKUP). Both only judge whether the
// marketplace still holds a linked code: they never write products, content, prices or stock.
public sealed partial class MarketplaceJobProcessor
{
    private const int ListingVerifyPageSize = 100;
    private const int ListingVerifyPagesPerSlice = 25;
    private static readonly TimeSpan ListingVerifySliceTime = TimeSpan.FromMinutes(4);
    private const int ListingLookupsPerSlice = 20;
    internal const string ListingLookupScanSource = "SCAN";
    internal const string ListingLookupPushSource = "PUSH";

    internal sealed record ListingLookupJobPayload(
        Guid ConnectionId,
        IReadOnlyList<Guid> VariantIds,
        string Source,
        bool ObserveOnly = false,
        Guid? RunJobId = null,
        IReadOnlyList<Guid>? NotFoundVariantIds = null,
        int Found = 0,
        int NotFound = 0,
        int Unknown = 0,
        string? RunSummary = null);

    // One slice of the scan: up to 25 pages or four minutes, then the job continues from the saved page. Every code on a
    // page counts as seen (archived, passive, draft and pending listings too: they exist). The last page finishes the run.
    private async Task<JobExecutionResult> VerifyListings(Guid tenantId, Guid connectionId, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null || !ListingVerificationPolicy.Supports(connection.PlatformCode))
            return JobExecutionResult.Blocked("LISTING_VERIFY_UNSUPPORTED", "Bu bağlantıda ürün doğrulama taraması yapılmaz.");
        if (connection.Environment is not ("STAGE" or "PRODUCTION"))
            return JobExecutionResult.Blocked("LISTING_VERIFY_UNSUPPORTED", "Ürün doğrulama taraması yalnız canlı ve test ortamı bağlantılarında yapılır.");
        var runJobId = jobId ?? Guid.CreateVersion7();
        var cursor = await Cursor(tenantId, connectionId, ListingVerificationPolicy.StateCursorResource, cancellationToken);
        var state = ListingVerificationState.Parse(cursor.OpaqueCursor);
        if (state.RunJobId != runJobId || state.StartedAt is null) state = state.Begin(runJobId, timeProvider.GetUtcNow());
        var observe = ListingVerificationPolicy.ObservesOnly(connection.PlatformCode, state.CompletedRuns);
        var sliceStarted = timeProvider.GetUtcNow();
        var slicePages = 0;
        while (true)
        {
            TrackRequest();
            var result = await products.ListCatalogAsync(
                Context(tenantId, connectionId, correlationId, $"listing-verify:{runJobId:N}:{state.Next ?? "0"}"),
                new(state.Next, ListingVerifyPageSize),
                new ProductReadFilter(null, IncludePendingApproval: true),
                cancellationToken);
            if (!result.IsSuccess)
            {
                TrackResultFailure(result.Error);
                await SaveListingVerifyState(cursor, state, cancellationToken);
                throw JobProcessingException.FromAdapter(result.Error!);
            }
            var page = result.Value!;
            foreach (var _ in page.Items) TrackReceived(changed: false);
            if (page.HasMore && string.IsNullOrWhiteSpace(page.NextCursor))
                throw new InvalidOperationException("Pazar yeri ürün sayfası hasMore=true ancak nextPageToken boş döndü.");
            // An observing run records only when a code was seen; it heals nothing.
            await MarkVariantLinksSeen(tenantId, connectionId, page.Items, cancellationToken, heal: !observe);
            state = state with { Next = page.HasMore ? page.NextCursor : null, Pages = state.Pages + 1, Received = state.Received + page.Items.Count };
            await SaveListingVerifyState(cursor, state, cancellationToken);
            slicePages++;
            if (!page.HasMore) break;
            if (slicePages >= ListingVerifyPagesPerSlice || timeProvider.GetUtcNow() - sliceStarted >= ListingVerifySliceTime)
                return JobExecutionResult.Continue($"Ürün doğrulama taraması sürüyor: {state.Pages:N0} sayfa, {state.Received:N0} ürün okundu.");
        }
        return await FinishListingVerification(tenantId, connection, state, observe, cancellationToken);
    }

    private async Task SaveListingVerifyState(SyncCursor cursor, ListingVerificationState state, CancellationToken cancellationToken)
    {
        cursor.OpaqueCursor = state.ToJson();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    // The links a scan may judge: healthy, suspect or unverified ones, of a variant the panel has not archived and
    // whose publication on the connection is not on its way (it may not be on the marketplace's lists yet).
    private IQueryable<MarketplaceVariantLink> VerifiableVariantLinks(Guid tenantId, Guid connectionId) =>
        JudgeableVariantLinks(tenantId, connectionId).Where(x => x.HealthStatus == null || x.HealthStatus == MarketplaceVariantLinkHealth.Suspect || x.HealthStatus == MarketplaceVariantLinkHealth.Unverified);

    // Every link of the connection whose variant the panel has not archived and whose publication is not on its way.
    private IQueryable<MarketplaceVariantLink> JudgeableVariantLinks(Guid tenantId, Guid connectionId)
    {
        var inFlight = MarketplaceVariantLinkHealth.PublicationInFlightStatuses;
        return db.MarketplaceVariantLinks.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
            && !db.ProductVariants.Any(variant => variant.TenantId == tenantId && variant.Id == x.VariantId
                && (variant.Status == ProductStatus.Archived || db.Products.Any(product => product.TenantId == tenantId && product.Id == variant.ProductId && product.Status == ProductStatus.Archived)))
            && !db.MarketplaceListingStates.Any(state => state.TenantId == tenantId && state.ConnectionId == connectionId && state.VariantId == x.VariantId && inFlight.Contains(state.ActualStatus))
            && !db.ChannelListingVariants.Any(listing => listing.TenantId == tenantId && listing.VariantId == x.VariantId
                && db.ChannelListingProfiles.Any(profile => profile.TenantId == tenantId && profile.Id == listing.ProfileId && profile.ConnectionId == connectionId
                    && (inFlight.Contains(listing.ActualStatus) || inFlight.Contains(profile.ActualStatus)))));
    }

    private sealed record UnseenLink(Guid Id, Guid VariantId, string ExternalId, string? ExternalSku, string? ExternalBarcode, string? HealthStatus, DateTimeOffset? HealthChangedAt, DateTimeOffset? LastSeenAt, DateTimeOffset? LastLookupAt, string? LastLookupOutcome);

    // The end of a complete scan. With the short-scan guard passed, a code missed for the first time becomes suspect and
    // goes to a single-code lookup the same night; a suspect code missed again whose lookup could not tell (or that has
    // no code to ask by) breaks (NOT_FOUND). Lookups are capped per run; the rest wait for the next scan. An observing
    // run (n11, Pazarama and PTT AVM, first two runs) only records what it would have done.
    private async Task<JobExecutionResult> FinishListingVerification(Guid tenantId, PlatformConnection connection, ListingVerificationState state, bool observe, CancellationToken cancellationToken)
    {
        var connectionId = connection.Id;
        var platform = connection.PlatformCode;
        var startedAt = state.StartedAt!.Value;
        var now = timeProvider.GetUtcNow();
        var candidates = VerifiableVariantLinks(tenantId, connectionId);
        var healthy = await candidates.CountAsync(cancellationToken);
        var seen = await candidates.CountAsync(x => x.LastSeenAt >= startedAt, cancellationToken);
        var listsVariants = ListingVerificationPolicy.ScanListsVariantCodes(platform);
        // PTT AVM's list cannot show variant codes, so its share of seen links proves nothing; its lookups decide alone.
        var complete = listsVariants ? MarketplaceVariantLinkHealth.FullScanCanMarkMissing(seen, healthy) : state.Received > 0;
        var platformName = ListingVerificationPolicy.PlatformName(platform);
        var newlyNotFound = new List<Guid>();
        var firstMisses = 0;
        var secondMisses = 0;
        var queued = new List<Guid>();
        if (healthy > 0 && !complete)
        {
            await RecordIssue(tenantId, $"variant-link-scan-short:{connectionId}", "VARIANT_LINK_SCAN_SHORT",
                $"{platformName} doğrulama taramasında bağlı {healthy:N0} varyant kodunun yalnız {seen:N0} tanesi görüldü; tarama eksik sayıldı, hiçbir kod kopuk işaretlenmedi ve tekil sorgu yapılmadı.", cancellationToken);
        }
        else if (healthy > 0)
        {
            await ResolveIssue(tenantId, $"variant-link-scan-short:{connectionId}", cancellationToken);
            var unseen = await candidates
                .Where(x => (x.LastSeenAt == null || x.LastSeenAt < startedAt) && (x.HealthChangedAt == null || x.HealthChangedAt < startedAt))
                .Select(x => new UnseenLink(x.Id, x.VariantId, x.ExternalId, x.ExternalSku, x.ExternalBarcode, x.HealthStatus, x.HealthChangedAt, x.LastSeenAt, x.LastLookupAt, x.LastLookupOutcome))
                .ToListAsync(cancellationToken);
            bool Askable(UnseenLink link) => ListingVerificationPolicy.LookupKey(platform, new MarketplaceVariantLink { ExternalId = link.ExternalId, ExternalSku = link.ExternalSku, ExternalBarcode = link.ExternalBarcode }) is not null;
            var unaskable = unseen.Where(link => !Askable(link)).Select(link => link.Id).ToArray();
            var secondMiss = listsVariants
                ? unseen.Where(link => link.HealthStatus == MarketplaceVariantLinkHealth.Suspect
                    && (!Askable(link) || link.LastLookupAt is { } lookedUp && link.HealthChangedAt is { } suspectedAt && lookedUp >= suspectedAt && MarketplaceListingLookupOutcome.Undecided(link.LastLookupOutcome))).ToList()
                : [];
            var firstMiss = listsVariants
                ? unseen.Where(link => link.HealthStatus is null or MarketplaceVariantLinkHealth.Unverified).ToList()
                : [];
            var secondMissIds = secondMiss.Select(link => link.Id).ToHashSet();
            queued = unseen
                .Where(link => !secondMissIds.Contains(link.Id) && Askable(link) && ListingVerificationPolicy.LookupDue(link.LastLookupAt, now))
                .OrderBy(link => link.LastLookupAt.HasValue)
                .ThenBy(link => link.LastSeenAt ?? DateTimeOffset.MinValue)
                .Take(ListingVerificationPolicy.MaxLookupsPerRun)
                .Select(link => link.VariantId)
                .ToList();
            firstMisses = firstMiss.Count;
            secondMisses = secondMiss.Count;
            // A link with no code the marketplace can be asked by is recorded as such, so the two-scan rule decides it.
            if (unaskable.Length > 0)
                await db.MarketplaceVariantLinks.Where(x => unaskable.Contains(x.Id))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastLookupAt, now).SetProperty(x => x.LastLookupOutcome, MarketplaceListingLookupOutcome.Unsupported), cancellationToken);
            if (!observe)
            {
                var secondIds = secondMissIds.ToArray();
                if (secondIds.Length > 0)
                {
                    await candidates.Where(x => secondIds.Contains(x.Id) && x.HealthStatus == MarketplaceVariantLinkHealth.Suspect && (x.HealthChangedAt == null || x.HealthChangedAt < startedAt))
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.HealthStatus, MarketplaceVariantLinkHealth.NotFound)
                            .SetProperty(x => x.HealthReason, MarketplaceVariantLinkHealth.NotFoundReason)
                            .SetProperty(x => x.HealthChangedAt, now), cancellationToken);
                    newlyNotFound.AddRange(secondMiss.Select(link => link.VariantId));
                }
                var firstIds = firstMiss.Select(link => link.Id).ToArray();
                if (firstIds.Length > 0)
                    await candidates.Where(x => firstIds.Contains(x.Id) && (x.HealthStatus == null || x.HealthStatus == MarketplaceVariantLinkHealth.Unverified) && (x.HealthChangedAt == null || x.HealthChangedAt < startedAt))
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.HealthStatus, MarketplaceVariantLinkHealth.Suspect)
                            .SetProperty(x => x.HealthReason, MarketplaceVariantLinkHealth.MissedByScanReason)
                            .SetProperty(x => x.HealthChangedAt, now), cancellationToken);
            }
        }

        var completedRun = state.CompletedRuns + 1;
        var summary = observe
            ? $"{platformName} doğrulama taraması (gözlem {completedRun}/{ListingVerificationPolicy.ObserveOnlyRuns}): {state.Received:N0} ürün okundu, bağlı {healthy:N0} koddan {seen:N0} tanesi görüldü"
                + (complete ? $"; {firstMisses:N0} kod şüpheli, {secondMisses:N0} kod bulunamadı işaretlenecekti, {queued.Count:N0} kod tekil sorguya alındı." : "; tarama eksik sayıldı.")
                + " Hiçbir durum değiştirilmedi."
            : $"{platformName} doğrulama taraması: {state.Received:N0} ürün okundu, bağlı {healthy:N0} koddan {seen:N0} tanesi görüldü"
                + (complete ? $"; {firstMisses:N0} kod ilk kez görülmedi, {newlyNotFound.Count:N0} kod bulunamadı işaretlendi, {queued.Count:N0} kod tekil sorguya alındı." : "; tarama eksik sayıldı, hiçbir kod işaretlenmedi.");
        if (observe) await RecordIssue(tenantId, $"listing-verify-observe:{connectionId}", "LISTING_VERIFY_OBSERVE", summary, cancellationToken);
        var cursor = await Cursor(tenantId, connectionId, ListingVerificationPolicy.StateCursorResource, cancellationToken);
        cursor.OpaqueCursor = state.Complete(now).ToJson();
        cursor.Version++;
        if (queued.Count > 0)
        {
            // One lookup job per run; the run's summary waits for its answers so the seller gets one notice.
            var payload = new ListingLookupJobPayload(connectionId, queued, ListingLookupScanSource, observe, state.RunJobId, newlyNotFound, RunSummary: summary);
            AddListingLookupJob(tenantId, connectionId, $"listing-lookup:{connectionId:N}:scan:{state.RunJobId:N}", payload, timeProvider.GetUtcNow());
        }
        else if (!observe && newlyNotFound.Count > 0)
            await RecordListingNotFoundSummary(tenantId, connectionId, platform, newlyNotFound, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.SuccessWithNote(observe ? "LISTING_VERIFY_OBSERVED" : "LISTING_VERIFY_COMPLETED", summary);
    }

    private void AddListingLookupJob(Guid tenantId, Guid connectionId, string dedup, ListingLookupJobPayload payload, DateTimeOffset now)
    {
        var json = JsonSerializer.Serialize(payload);
        db.IntegrationJobs.Add(new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = MarketplaceJobTypes.ListingLookup,
            PayloadJson = json,
            PayloadVersion = 1,
            PayloadHash = Hash(json),
            JobDedupKey = dedup,
            EffectIdempotencyKey = dedup,
            Priority = JobPriorityPolicy.For(MarketplaceJobTypes.ListingLookup, userInitiated: false),
            Status = JobStatus.Pending,
            AvailableAt = now,
            CorrelationId = $"listing-lookup:{connectionId:N}",
            CreatedAt = now,
            Version = 1
        });
    }

    // Codes a price/stock answer refused as unknown are asked about at once, one lookup job per variant; a variant
    // already asked within the last hours (or waiting for its lookup) is not asked again.
    private async Task QueueListingLookupsForRefusedCodes(Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0) return;
        var now = timeProvider.GetUtcNow();
        var ids = variantIds.ToArray();
        var due = (await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && ids.Contains(x.VariantId))
                .Select(x => new { x.VariantId, x.LastLookupAt })
                .ToListAsync(cancellationToken))
            .Where(x => ListingVerificationPolicy.LookupDue(x.LastLookupAt, now))
            .Select(x => x.VariantId)
            .ToList();
        var bucket = now.ToUnixTimeSeconds() / (long)ListingVerificationPolicy.LookupSpacing.TotalSeconds;
        foreach (var variantId in due)
        {
            var dedup = $"listing-lookup:{connectionId:N}:push:{variantId:N}:{bucket}";
            if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ListingLookup && x.JobDedupKey == dedup, cancellationToken)) continue;
            AddListingLookupJob(tenantId, connectionId, dedup, new ListingLookupJobPayload(connectionId, [variantId], ListingLookupPushSource), now);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    // One slice of single-code lookups. Each answer is recorded on the link; outside an observing run a code the
    // marketplace holds is healthy again and a code a well-formed answer does not hold breaks at once (NOT_FOUND).
    // An error, an ambiguous answer or a platform that cannot be asked leaves the health as it was.
    private async Task<JobExecutionResult> RunListingLookups(Guid tenantId, Guid connectionId, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var job = jobId is { } id ? await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken) : null;
        ListingLookupJobPayload? payload;
        try { payload = job is null ? null : JsonSerializer.Deserialize<ListingLookupJobPayload>(job.PayloadJson, JsonOptions); }
        catch (JsonException) { payload = null; }
        if (job is null || payload is null || payload.ConnectionId != connectionId)
            return JobExecutionResult.Blocked("LISTING_LOOKUP_PAYLOAD_INVALID", "Tekil ürün sorgusu işi payload sözleşmesini sağlamıyor.");
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (!ListingVerificationPolicy.Supports(platform)) return JobExecutionResult.Blocked("LISTING_VERIFY_UNSUPPORTED", "Bu bağlantıda tekil ürün sorgusu yapılmaz.");
        var queuedAt = job.CreatedAt;
        var remaining = payload.VariantIds.ToList();
        var notFound = (payload.NotFoundVariantIds ?? []).ToList();
        var (found, missing, unknown) = (payload.Found, payload.NotFound, payload.Unknown);
        var sliceCount = 0;
        while (remaining.Count > 0 && sliceCount < ListingLookupsPerSlice)
        {
            var variantId = remaining[0];
            var now = timeProvider.GetUtcNow();
            var link = await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId, cancellationToken);
            // A code changed by hand after the lookup was queued, or one asked again meanwhile, is left alone; so is a
            // variant whose publication is under way or that the panel archived.
            var askable = link is not null
                && (link.HealthChangedAt is null || link.HealthChangedAt <= queuedAt)
                && (link.LastLookupAt is null || link.LastLookupAt < queuedAt)
                && await JudgeableVariantLinks(tenantId, connectionId).AnyAsync(x => x.Id == link.Id, cancellationToken);
            if (askable)
            {
                var outcome = await LookUpListing(tenantId, connectionId, platform!, link!, correlationId, cancellationToken);
                link!.LastLookupAt = now;
                link.LastLookupOutcome = outcome;
                link.Version++;
                if (outcome == MarketplaceListingLookupOutcome.Found)
                {
                    found++;
                    if (!payload.ObserveOnly) MarketplaceVariantLinkHealth.MarkSeen(link, now);
                }
                else if (outcome == MarketplaceListingLookupOutcome.NotFound)
                {
                    missing++;
                    if (!payload.ObserveOnly && link.HealthStatus != MarketplaceVariantLinkHealth.NotFound)
                    {
                        link.HealthStatus = MarketplaceVariantLinkHealth.NotFound;
                        link.HealthReason = MarketplaceVariantLinkHealth.NotFoundByLookupReason;
                        link.HealthChangedAt = now;
                        notFound.Add(variantId);
                    }
                }
                else unknown++;
            }
            remaining.RemoveAt(0);
            sliceCount++;
            job.PayloadJson = JsonSerializer.Serialize(payload with { VariantIds = remaining, NotFoundVariantIds = notFound, Found = found, NotFound = missing, Unknown = unknown });
            job.PayloadHash = Hash(job.PayloadJson);
            await db.SaveChangesAsync(cancellationToken);
        }
        if (remaining.Count > 0)
            return JobExecutionResult.Continue($"Tekil ürün sorgusu sürüyor: {found + missing + unknown:N0} kod soruldu, {remaining.Count:N0} kod kaldı.");

        var lookupSummary = $"Tekil sorgu: {found:N0} kod bulundu, {missing:N0} kod bulunamadı, {unknown:N0} kod belirsiz kaldı.";
        if (payload.ObserveOnly)
        {
            await RecordIssue(tenantId, $"listing-verify-observe:{connectionId}", "LISTING_VERIFY_OBSERVE",
                Short($"{payload.RunSummary} {lookupSummary} Hiçbir durum değiştirilmedi.", 2000), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.SuccessWithNote("LISTING_LOOKUP_OBSERVED", lookupSummary);
        }
        if (notFound.Count > 0)
        {
            await RecordListingNotFoundSummary(tenantId, connectionId, platform, notFound, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        return JobExecutionResult.SuccessWithNote("LISTING_LOOKUP_COMPLETED", lookupSummary);
    }

    // Asks the marketplace about one link by the code its lookup takes (ListingVerificationPolicy.LookupKey). A quota or
    // credential refusal stops the job (it is retried with the remaining codes); any other failure is an unknown answer.
    private async Task<string> LookUpListing(Guid tenantId, Guid connectionId, string platform, MarketplaceVariantLink link, string correlationId, CancellationToken cancellationToken)
    {
        if (ListingVerificationPolicy.LookupKey(platform, link) is not { } key) return MarketplaceListingLookupOutcome.Unsupported;
        var context = Context(tenantId, connectionId, correlationId, $"listing-lookup:{link.Id:N}");
        if (platform == "PTTAVM")
        {
            if (products is not IListingCodeCheckPort codeCheck) return MarketplaceListingLookupOutcome.Unsupported;
            TrackRequest();
            var held = await codeCheck.HoldsCodeAsync(context, key.Code, cancellationToken);
            if (!held.IsSuccess) return LookupFailure(held.Error!);
            return held.Value switch { true => MarketplaceListingLookupOutcome.Found, false => MarketplaceListingLookupOutcome.NotFound, _ => MarketplaceListingLookupOutcome.Unknown };
        }
        var filter = key.AsProductMainId
            ? new ProductReadFilter(null, ProductMainId: key.Code, IncludePendingApproval: true)
            : new ProductReadFilter(null, Barcode: key.Code, IncludePendingApproval: true);
        var codes = new[] { link.ExternalId, link.ExternalSku, link.ExternalBarcode, key.Code }.Where(code => !MarketplaceVariantLinkHealth.IsListingPlaceholder(code));
        var variants = new List<(string? Id, string? Sku, string? Barcode)>();
        string? next = null;
        var answerComplete = false;
        for (var page = 0; page < ListingVerificationPolicy.MaxLookupPages; page++)
        {
            TrackRequest();
            var result = await products.ListCatalogAsync(context, new(next, ListingVerifyPageSize), filter, cancellationToken);
            if (!result.IsSuccess) return LookupFailure(result.Error!);
            variants.AddRange(result.Value!.Items.SelectMany(item => item.Variants).Select(variant => ((string?)variant.ExternalVariantId, (string?)variant.Sku, variant.Barcode)));
            if (ListingVerificationPolicy.Outcome(codes, variants, answerComplete: false) == MarketplaceListingLookupOutcome.Found) return MarketplaceListingLookupOutcome.Found;
            if (!result.Value.HasMore) { answerComplete = true; break; }
            next = result.Value.NextCursor;
            if (string.IsNullOrWhiteSpace(next)) break;
        }
        return ListingVerificationPolicy.Outcome(codes, variants, answerComplete);
    }

    private string LookupFailure(AdapterError error)
    {
        TrackResultFailure(error);
        if (error.Class is AdapterErrorClass.RateLimit or AdapterErrorClass.Authentication) throw JobProcessingException.FromAdapter(error);
        return MarketplaceListingLookupOutcome.Unknown;
    }

    // The one notice of a run: "Trendyol: 3 ürün pazaryerinde bulunamadı." counted by the panel products (archived ones
    // left out) of the variants that newly broke.
    private async Task RecordListingNotFoundSummary(Guid tenantId, Guid connectionId, string? platform, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        var ids = variantIds.Distinct().ToArray();
        var live = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.Status != ProductStatus.Archived
                && db.Products.Any(product => product.TenantId == tenantId && product.Id == x.ProductId && product.Status != ProductStatus.Archived))
            .Select(x => new { x.Id, x.ProductId })
            .ToListAsync(cancellationToken);
        if (live.Count == 0) return;
        await RecordIssue(tenantId, $"listing-not-found:{connectionId}", "LISTING_NOT_FOUND",
            ListingVerificationPolicy.NotFoundSummary(platform, live.Select(x => x.ProductId).Distinct().Count(), live.Count), cancellationToken);
    }
}
