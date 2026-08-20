using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Imports;

public sealed class ImportService(AppDbContext db, IPrivateFileStorage files, CursorCodec cursors, TimeProvider timeProvider, IConfiguration configuration) : IImportService
{
    private readonly long _maximumBytes = configuration.GetValue<long?>("Storage:MaxUploadBytes") ?? 10 * 1024 * 1024;
    private static readonly HashSet<string> TargetFields = new(StringComparer.Ordinal) { "title", "description", "sku", "barcode", "modelCode", "stock", "listPrice", "salePrice", "currency", "externalId", "variantGroupKey" };

    public async Task<PageResult<ImportSessionView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.ImportSessions.AsNoTracking().Where(x => x.TenantId == tenantId); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0); var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken); return Page(rows, limit, Map);
    }

    public async Task<ServiceResult<ImportSessionView>> CreateAsync(Guid tenantId, CreateImportCommand command, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ImportSourceType>(command.SourceType, true, out var source)) return Invalid<ImportSessionView>("sourceType", "Kaynak MARKETPLACE, CSV veya XLSX olmalıdır.");
        if (source == ImportSourceType.Marketplace && command.ConnectionId is null) return Invalid<ImportSessionView>("connectionId", "MARKETPLACE import bir connection ister.");
        if (command.ConnectionId is Guid connectionId && !await db.PlatformConnections.AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken)) return NotFound<ImportSessionView>();
        var now = timeProvider.GetUtcNow(); var session = new ImportSession { Id = Guid.CreateVersion7(), TenantId = tenantId, SourceType = source, ConnectionId = command.ConnectionId, CreatedAt = now, UpdatedAt = now }; db.ImportSessions.Add(session); await db.SaveChangesAsync(cancellationToken); return ServiceResult<ImportSessionView>.Ok(Map(session));
    }

    public async Task<ServiceResult<ImportSessionView>> AttachSourceAsync(Guid tenantId, Guid sessionId, ImportUpload upload, CancellationToken cancellationToken)
    {
        var session = await db.ImportSessions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sessionId, cancellationToken); if (session is null) return NotFound<ImportSessionView>(); if (session.SourceType == ImportSourceType.Marketplace) return Conflict<ImportSessionView>("IMPORT_SOURCE_FILE_NOT_ALLOWED", "MARKETPLACE kaynağı source-file kabul etmez."); if (session.Status != ImportSessionStatus.Created) return Conflict<ImportSessionView>("IMPORT_STATE_CONFLICT", "Kaynak dosya yalnız CREATED durumda eklenebilir.");
        if (upload.Length is <= 0 || upload.Length > _maximumBytes) return Invalid<ImportSessionView>("file", "Dosya genel 10 MiB upload üst sınırını aşıyor veya boş.");
        await using var buffer = new MemoryStream((int)upload.Length); await upload.Content.CopyToAsync(buffer, cancellationToken); if (buffer.Length > _maximumBytes) return Invalid<ImportSessionView>("file", "Dosya genel 10 MiB upload üst sınırını aşıyor."); buffer.Position = 0;
        var expectedMime = session.SourceType == ImportSourceType.Csv ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; if (!string.Equals(upload.ContentType, expectedMime, StringComparison.OrdinalIgnoreCase)) return Invalid<ImportSessionView>("file", "Dosya MIME türü import kaynağıyla eşleşmiyor.");
        var prefix = buffer.ToArray().AsSpan(0, (int)Math.Min(4, buffer.Length)); if (session.SourceType == ImportSourceType.Xlsx && (prefix.Length < 4 || prefix[0] != (byte)'P' || prefix[1] != (byte)'K')) return Invalid<ImportSessionView>("file", "XLSX magic-byte doğrulaması başarısız.");
        var hash = Convert.ToHexString(SHA256.HashData(buffer.ToArray())); var existing = await db.FileAssets.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Classification == "IMPORT_SOURCE" && x.Sha256 == hash && x.ArchivedAt == null, cancellationToken);
        if (existing is null)
        {
            var assetId = Guid.CreateVersion7(); var extension = session.SourceType == ImportSourceType.Csv ? ".csv" : ".xlsx"; buffer.Position = 0; var stored = await files.SaveAsync(tenantId, $"{assetId:N}{extension}", expectedMime, buffer, _maximumBytes, cancellationToken); existing = new FileAsset { Id = assetId, TenantId = tenantId, Classification = "IMPORT_SOURCE", RelativePath = stored, OriginalNameSafe = Path.GetFileName(upload.FileName), MimeType = expectedMime, SizeBytes = buffer.Length, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() }; db.FileAssets.Add(existing);
        }
        session.SourceAssetId = existing.Id; session.UpdatedAt = timeProvider.GetUtcNow(); session.Version++; await db.SaveChangesAsync(cancellationToken); return ServiceResult<ImportSessionView>.Ok(Map(session));
    }

    public async Task<ServiceResult<ImportSessionView>> ConfigureColumnsAsync(Guid tenantId, Guid sessionId, long expectedVersion, UpdateColumnMappingCommand command, CancellationToken cancellationToken)
    {
        var session = await db.ImportSessions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sessionId, cancellationToken); if (session is null) return NotFound<ImportSessionView>(); if (session.Version != expectedVersion) return Precondition<ImportSessionView>(session.Version); if (session.SourceType == ImportSourceType.Marketplace) return Conflict<ImportSessionView>("IMPORT_COLUMN_MAPPING_NOT_ALLOWED", "MARKETPLACE kaynağı dosya kolon eşlemesi kullanmaz."); if (session.SourceAssetId is null) return Conflict<ImportSessionView>("IMPORT_SOURCE_REQUIRED", "Önce kaynak dosya yüklenmelidir.");
        if (command.Mappings.Count == 0 || command.Mappings.Any(x => string.IsNullOrWhiteSpace(x.SourceColumn) || !TargetFields.Contains(x.TargetField)) || command.Mappings.Select(x => x.TargetField).Distinct(StringComparer.Ordinal).Count() != command.Mappings.Count) return Invalid<ImportSessionView>("mappings", "Kolon eşlemesi boş, tekrarlı veya izinli hedef alan dışında.");
        var profile = session.ColumnProfileId is Guid profileId ? await db.ImportColumnProfiles.SingleAsync(x => x.TenantId == tenantId && x.Id == profileId, cancellationToken) : new ImportColumnProfile { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = command.ProfileName.Trim(), CreatedAt = timeProvider.GetUtcNow() };
        if (session.ColumnProfileId is null) { db.ImportColumnProfiles.Add(profile); session.ColumnProfileId = profile.Id; } else { profile.Name = command.ProfileName.Trim(); profile.Version++; var current = await db.ImportColumnMappings.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).ToListAsync(cancellationToken); db.ImportColumnMappings.RemoveRange(current); }
        db.ImportColumnMappings.AddRange(command.Mappings.Select(x => new ImportColumnMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ProfileId = profile.Id, SourceColumn = x.SourceColumn.Trim(), TargetField = x.TargetField, SortOrder = x.SortOrder })); session.VariantGroupKey = string.IsNullOrWhiteSpace(command.VariantGroupKey) ? null : command.VariantGroupKey.Trim(); session.UpdatedAt = timeProvider.GetUtcNow(); session.Version++; await db.SaveChangesAsync(cancellationToken); return ServiceResult<ImportSessionView>.Ok(Map(session));
    }

    public async Task<ServiceResult<Guid>> EnqueuePreviewAsync(Guid tenantId, Guid sessionId, string correlationId, CancellationToken cancellationToken)
    {
        var session = await db.ImportSessions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sessionId, cancellationToken); if (session is null) return NotFound<Guid>(); if (session.SourceType == ImportSourceType.Marketplace) return ServiceResult<Guid>.Fail("CAPABILITY_UNKNOWN", "MARKETPLACE import Trendyol adapter capability kanıtı olmadan başlatılamaz.", 422); if (session.SourceAssetId is null || session.ColumnProfileId is null || session.Status != ImportSessionStatus.Created) return Conflict<Guid>("IMPORT_NOT_READY", "Preview için kaynak dosya, kolon eşlemesi ve CREATED durum gerekir.");
        var dedup = $"import-preview:{session.Id}:v{session.Version}"; var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == "IMPORT_PREVIEW" && x.JobDedupKey == dedup, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var payload = JsonSerializer.Serialize(new { sessionId = session.Id, operation = "PREVIEW" }); var job = Job(tenantId, "IMPORT_PREVIEW", dedup, payload, correlationId); db.IntegrationJobs.Add(job); ImportStateMachine.Transition(session, ImportSessionStatus.Fetching); session.UpdatedAt = timeProvider.GetUtcNow(); session.Version++; await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    public async Task<ServiceResult<ImportSessionView>> GetAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.ImportSessions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sessionId, cancellationToken); return session is null ? NotFound<ImportSessionView>() : ServiceResult<ImportSessionView>.Ok(Map(session));
    }

    public async Task<PageResult<ImportCandidateView>> CandidatesAsync(Guid tenantId, Guid sessionId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after); var query = db.ImportMatchCandidates.AsNoTracking().Where(x => x.TenantId == tenantId && x.SessionId == sessionId); if (afterId != Guid.Empty) query = query.Where(x => x.Id.CompareTo(afterId) > 0); var rows = await query.OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken); return Page(rows, limit, CandidateMap);
    }

    public async Task<ServiceResult<ImportCandidateView>> DecideAsync(Guid tenantId, Guid userId, Guid sessionId, Guid candidateId, long expectedVersion, ImportDecisionCommand command, CancellationToken cancellationToken)
    {
        var candidate = await db.ImportMatchCandidates.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.SessionId == sessionId && x.Id == candidateId, cancellationToken); if (candidate is null) return NotFound<ImportCandidateView>(); if (candidate.Version != expectedVersion) return Precondition<ImportCandidateView>(candidate.Version); if (!Enum.TryParse<ImportDecisionKind>(command.Decision, true, out var decision)) return Invalid<ImportCandidateView>("decision", "Karar CREATE, LINK veya SKIP olmalıdır.");
        if (decision == ImportDecisionKind.Link && (command.ProductId is null || command.VariantId is null || !await db.ProductVariants.AnyAsync(x => x.TenantId == tenantId && x.Id == command.VariantId && x.ProductId == command.ProductId, cancellationToken))) return Invalid<ImportCandidateView>("variantId", "LINK kararı tenant içindeki geçerli product/variant ister.");
        var current = await db.ImportDecisions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CandidateId == candidateId, cancellationToken); if (current is null) { current = new ImportDecision { Id = Guid.CreateVersion7(), TenantId = tenantId, CandidateId = candidateId, ActorUserId = userId, DecidedAt = timeProvider.GetUtcNow() }; db.ImportDecisions.Add(current); } else { current.Version++; current.DecidedAt = timeProvider.GetUtcNow(); current.ActorUserId = userId; }
        current.Decision = decision; current.LinkProductId = command.ProductId; current.LinkVariantId = command.VariantId; candidate.Status = "DECIDED"; candidate.Version++;
        var session = await db.ImportSessions.SingleAsync(x => x.TenantId == tenantId && x.Id == sessionId, cancellationToken); var candidateCount = await db.ImportMatchCandidates.CountAsync(x => x.TenantId == tenantId && x.SessionId == sessionId, cancellationToken); var pendingAddition = db.Entry(current).State == EntityState.Added ? 1 : 0; var decisionCount = await db.ImportDecisions.CountAsync(x => x.TenantId == tenantId && db.ImportMatchCandidates.Where(candidateRow => candidateRow.SessionId == sessionId).Select(candidateRow => candidateRow.Id).Contains(x.CandidateId), cancellationToken) + pendingAddition; if (decisionCount >= candidateCount) ImportStateMachine.Transition(session, ImportSessionStatus.ReadyToApply); session.UpdatedAt = timeProvider.GetUtcNow(); session.Version++; await db.SaveChangesAsync(cancellationToken); return ServiceResult<ImportCandidateView>.Ok(CandidateMap(candidate));
    }

    public async Task<ServiceResult<Guid>> EnqueueApplyAsync(Guid tenantId, Guid sessionId, string correlationId, CancellationToken cancellationToken)
    {
        var session = await db.ImportSessions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sessionId, cancellationToken); if (session is null) return NotFound<Guid>(); if (session.Status != ImportSessionStatus.ReadyToApply) return Conflict<Guid>("IMPORT_DECISIONS_REQUIRED", "Tüm candidate kararları tamamlanmalıdır."); var dedup = $"import-apply:{session.Id}:v{session.Version}"; var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == "IMPORT_APPLY" && x.JobDedupKey == dedup, cancellationToken); if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id); var payload = JsonSerializer.Serialize(new { sessionId = session.Id, operation = "APPLY" }); var job = Job(tenantId, "IMPORT_APPLY", dedup, payload, correlationId); db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    public async Task<ServiceResult<string>> BuildErrorsCsvAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await db.ImportSessions.AnyAsync(x => x.TenantId == tenantId && x.Id == sessionId, cancellationToken)) return NotFound<string>(); var rows = await db.ImportStagingRecords.AsNoTracking().Where(x => x.TenantId == tenantId && x.SessionId == sessionId && x.ValidationErrorsJson != "[]").OrderBy(x => x.RowNumber).Select(x => new { x.RowNumber, x.ValidationErrorsJson }).ToListAsync(cancellationToken); var builder = new StringBuilder("rowNumber,errors\r\n"); foreach (var row in rows) builder.Append(row.RowNumber).Append(',').Append(ImportCsvSecurity.Neutralize(row.ValidationErrorsJson)).Append("\r\n"); return ServiceResult<string>.Ok(builder.ToString());
    }

    private IntegrationJob Job(Guid tenantId, string type, string dedup, string payload, string correlationId) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, JobType = type, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), JobDedupKey = dedup, EffectIdempotencyKey = dedup, AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 };
    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private PageResult<TView> Page<TEntity, TView>(List<TEntity> rows, int limit, Func<TEntity, TView> map) where TEntity : class { var hasMore = rows.Count > limit; var items = rows.Take(limit).Select(map).ToList(); var next = hasMore ? cursors.Encode((Guid)typeof(TEntity).GetProperty("Id")!.GetValue(rows[limit - 1])!) : null; return new(items, next, hasMore); }
    private static ImportSessionView Map(ImportSession value) => new(value.Id, value.SourceType.ToString().ToUpperInvariant(), Status(value.Status), value.SourceAssetId, value.TotalRows, value.ValidRows, value.ErrorRows, value.ReviewRows, value.UpdatedAt, value.Version);
    private static ImportCandidateView CandidateMap(ImportMatchCandidate value) => new(value.Id, value.StagingRecordId, value.ProductId, value.VariantId, value.MatchRule, value.Status, value.SafeSummary, value.Version);
    private static string Status(ImportSessionStatus value) => value switch { ImportSessionStatus.ReviewRequired => "REVIEW_REQUIRED", ImportSessionStatus.ReadyToApply => "READY_TO_APPLY", ImportSessionStatus.PartiallyCompleted => "PARTIALLY_COMPLETED", _ => value.ToString().ToUpperInvariant() };
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Conflict<T>(string code, string message) => ServiceResult<T>.Fail(code, message, 409);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{version}.", 412);
}

public static class ImportCsvSecurity
{
    public static string Neutralize(string value)
    {
        var trimmed = value.TrimStart();
        var safe = trimmed.StartsWith('=') || trimmed.StartsWith('+') || trimmed.StartsWith('-') || trimmed.StartsWith('@') ? "'" + value : value;
        return '"' + safe.Replace("\"", "\"\"") + '"';
    }
}
