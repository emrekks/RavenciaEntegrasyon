export type HepsiburadaValueSyncState = {
  allowsCustomValue: boolean | null | undefined
  snapshotMissing: boolean
  syncPending: boolean
  alreadyRequested: boolean
}

export function shouldAutoSyncHepsiburadaValues(state: HepsiburadaValueSyncState) {
  return state.allowsCustomValue === false
    && state.snapshotMissing
    && !state.syncPending
    && !state.alreadyRequested
}

export function isReferenceSyncJobTerminal(status: string | undefined) {
  return status === 'SUCCEEDED'
    || status === 'BLOCKED'
    || status === 'MANUAL_REVIEW'
    || status === 'DEAD'
    || status === 'CANCELLED'
}

// How often the sync job a value list waits on is read again: every second while it runs, never again once it has ended
// or could not be read (a job that cannot be read would otherwise be asked for every second, forever, with the button locked).
export function referenceSyncJobPollInterval(status: string | undefined, readFailed: boolean): number | false {
  if (readFailed || isReferenceSyncJobTerminal(status)) return false
  return 1000
}

// Polls value-list sync jobs until each has ended or the time is up; returns the end state of those that ended.
// A job still running at the deadline is simply missing from the result (its field stays unmatched, nothing guessed).
export async function waitForReferenceSyncJobs(
  jobIds: readonly string[],
  readStatus: (jobId: string) => Promise<string | undefined>,
  { intervalMs = 2_000, timeoutMs = 180_000, sleep = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms)) }: { intervalMs?: number; timeoutMs?: number; sleep?: (ms: number) => Promise<void> } = {}
) {
  const ended = new Map<string, string>()
  for (let waited = 0; ; waited += intervalMs) {
    for (const jobId of jobIds) {
      if (ended.has(jobId)) continue
      const status = await readStatus(jobId).catch(() => undefined)
      if (status && isReferenceSyncJobTerminal(status)) ended.set(jobId, status)
    }
    if (ended.size === jobIds.length || waited >= timeoutMs) return ended
    await sleep(intervalMs)
  }
}
