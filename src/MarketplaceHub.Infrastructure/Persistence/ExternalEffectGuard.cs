using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-10 (15 Nov 2026): the one place a job's marketplace write opens, sends and closes its ExternalEffect record.
// The rules every write follows:
// - The record is saved before the request leaves, and nothing but the marketplace call runs between that save and the
//   send (ISLEYICI-6): a failed database read can no longer leave a request that was never sent as an unclear effect.
// - An accepted request completes the record (CompletedAt); the caller saves it together with its own state.
// - An unclear answer (WriteOutcomePolicy.IsUncertain) leaves the record open, so a later run reads the result back or
//   stops for review instead of sending again. A cancelled or thrown send leaves it open the same way.
// - A request known not to be applied (the panel's own open circuit, a quota answer, a refusal) has its record released,
//   so the next run may send it again; the caller writes what the refusal means for its own state (ISLEYICI-4: a quota
//   answer keeps a return decision pending, only a lasting refusal fails it).
// What an existing record means before a send (Gate) and what each answer means for the job stay with each write: they
// differ between writes on purpose (see ExternalEffectSendOutcome and the call sites).
internal sealed class ExternalEffectGuard(AppDbContext db, TimeProvider timeProvider)
{
    // A write's existing record, tracked so the caller may reopen, complete or release it.
    public Task<ExternalEffectRecord?> FindAsync(Guid tenantId, string effectType, string idempotencyKey, CancellationToken cancellationToken) =>
        db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == effectType && x.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<bool> ExistsAsync(Guid tenantId, string effectType, string idempotencyKey, CancellationToken cancellationToken) =>
        db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == effectType && x.IdempotencyKey == idempotencyKey, cancellationToken);

    // What an existing record says about a write about to be sent.
    public static ExternalEffectGate Gate(ExternalEffectRecord? existing) =>
        existing is null ? ExternalEffectGate.None
        : existing.CompletedAt is null ? ExternalEffectGate.Open
        : existing.Outcome == ExternalEffectReconcilePolicy.UnresolvedOutcome ? ExternalEffectGate.Unresolved
        : ExternalEffectGate.Done;

    // A new record; the context (connection, target, action, expected value) lets the sweep verify an unclear outcome.
    public ExternalEffectRecord New(Guid tenantId, string effectType, string idempotencyKey, Guid? connectionId = null, Guid? targetId = null, string? action = null, string? expectedValue = null, DateTimeOffset? createdAt = null) =>
        new() { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = idempotencyKey, CreatedAt = createdAt ?? timeProvider.GetUtcNow(), ConnectionId = connectionId, TargetId = targetId, Action = action, ExpectedValue = expectedValue };

    // Saves the record (adding it when it is new; an existing one is saved as the caller left it) and sends. Nothing runs
    // between the save and the send. saveBeforeSend false only for a record already stored open and left as it was (a
    // resend that continues an earlier, known-incomplete send).
    public Task<ExternalEffectSend<T>> SendAsync<T>(ExternalEffectRecord effect, Func<Task<AdapterResult<T>>> send, CancellationToken cancellationToken, bool saveBeforeSend = true) =>
        SendAsync([effect], send, cancellationToken, saveBeforeSend);

    // One request that carries several writes (a grouped send): every record is opened, completed or left open together.
    public async Task<ExternalEffectSend<T>> SendAsync<T>(IReadOnlyList<ExternalEffectRecord> effects, Func<Task<AdapterResult<T>>> send, CancellationToken cancellationToken, bool saveBeforeSend = true)
    {
        foreach (var effect in effects)
            if (db.Entry(effect).State == EntityState.Detached) db.ExternalEffectRecords.Add(effect);
        if (saveBeforeSend) await db.SaveChangesAsync(cancellationToken);
        var result = await send();
        var answeredAt = timeProvider.GetUtcNow();
        if (result.IsSuccess)
        {
            foreach (var effect in effects)
            {
                effect.SentAt = answeredAt;
                effect.CompletedAt = answeredAt;
            }
            return new(result, ExternalEffectSendOutcome.Accepted);
        }
        var outcome = Classify(result.Error!);
        // KUYRUK-2: an unclear answer leaves the record open for the sweep, which waits from this moment (the request went
        // out by now) instead of from the record's creation, before any quota wait. Written on its own, as the caller
        // does not always save after an unclear answer; a failure here only leaves the old measure (CreatedAt).
        if (outcome == ExternalEffectSendOutcome.Uncertain) await MarkSentAsync(effects, answeredAt);
        return new(result, outcome);
    }

    private async Task MarkSentAsync(IReadOnlyList<ExternalEffectRecord> effects, DateTimeOffset answeredAt)
    {
        var ids = effects.Select(effect => effect.Id).ToArray();
        // Inside the caller's open transaction a failed or cancelled statement would abort that transaction even with its
        // exception swallowed; there the time is only set on the tracked records and goes with the caller's own save.
        // (No caller sends inside a transaction today: the processors hold a session advisory lock, the instant actions
        // send before theirs.)
        if (db.Database.CurrentTransaction is not null)
        {
            foreach (var effect in effects) effect.SentAt ??= answeredAt;
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await db.ExternalEffectRecords.Where(x => ids.Contains(x.Id) && x.SentAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.SentAt, answeredAt.ToUniversalTime()), timeout.Token);
            foreach (var effect in effects) effect.SentAt ??= answeredAt;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    // Unclear first (the request may have been applied), then the panel's own circuit (never sent), then a refusal the job
    // retries (a quota answer) and a lasting refusal.
    public static ExternalEffectSendOutcome Classify(AdapterError error) =>
        WriteOutcomePolicy.IsUncertain(error) ? ExternalEffectSendOutcome.Uncertain
        : LocalCircuitOpen.IsCode(error.Code) ? ExternalEffectSendOutcome.LocalCircuit
        : JobExecutionResult.FromAdapterError(error).Kind == JobCompletionKind.Retry ? ExternalEffectSendOutcome.Retryable
        : ExternalEffectSendOutcome.Refused;

    // The request was not applied: the record goes, so the next run may send again. Saved with the caller's next save.
    public void Release(ExternalEffectRecord effect) => db.ExternalEffectRecords.Remove(effect);

    public void Release(IEnumerable<ExternalEffectRecord> effects) => db.ExternalEffectRecords.RemoveRange(effects);

    public async Task ReleaseAsync(ExternalEffectRecord effect, CancellationToken cancellationToken)
    {
        Release(effect);
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal enum ExternalEffectGate
{
    // No record: the write was never sent under this key.
    None,
    // Sent with an outcome nobody confirmed yet (unclear answer, cut off): never sent again automatically.
    Open,
    // Closed by the sweep without a confirmed outcome: verified by a read where one can judge it, never sent again.
    Unresolved,
    // Sent and accepted (or verified applied).
    Done
}

internal enum ExternalEffectSendOutcome
{
    Accepted,
    // The request may have been applied: the record stays open.
    Uncertain,
    // The panel's own open circuit refused it before it left: not applied.
    LocalCircuit,
    // A refusal the job retries (a quota answer): not applied.
    Retryable,
    // A lasting refusal: not applied.
    Refused
}

internal readonly record struct ExternalEffectSend<T>(AdapterResult<T> Result, ExternalEffectSendOutcome Outcome)
{
    public bool Accepted => Outcome == ExternalEffectSendOutcome.Accepted;
    public T Value => Result.Value!;
    public AdapterError Error => Result.Error!;
}
