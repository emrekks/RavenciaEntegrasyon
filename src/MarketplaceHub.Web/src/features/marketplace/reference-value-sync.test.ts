import { describe, expect, it } from 'vitest'
import { isReferenceSyncJobTerminal, referenceSyncJobPollInterval, shouldAutoSyncHepsiburadaValues, waitForReferenceSyncJobs } from './reference-value-sync'

describe('following a value-list sync job', () => {
  it('reads every second while the job runs or has not answered yet', () => {
    expect(referenceSyncJobPollInterval(undefined, false)).toBe(1000)
    expect(referenceSyncJobPollInterval('RUNNING', false)).toBe(1000)
  })

  it('stops once the job has ended', () => {
    expect(referenceSyncJobPollInterval('SUCCEEDED', false)).toBe(false)
    expect(referenceSyncJobPollInterval('DEAD', false)).toBe(false)
  })

  it('stops when the job cannot be read, so it is not asked for every second forever', () => {
    expect(referenceSyncJobPollInterval(undefined, true)).toBe(false)
    expect(referenceSyncJobPollInterval('RUNNING', true)).toBe(false)
  })
})

describe('waiting for value-list syncs', () => {
  it('polls until every job has ended and leaves a job still running at the deadline out', async () => {
    const reads: Record<string, string[]> = { a: ['QUEUED', 'RUNNING', 'SUCCEEDED'], b: ['RUNNING'] }
    const sleep = async () => {}
    const ended = await waitForReferenceSyncJobs(['a', 'b'], async id => reads[id].length > 1 ? reads[id].shift() : reads[id][0], { intervalMs: 1, timeoutMs: 5, sleep })
    expect([...ended]).toEqual([['a', 'SUCCEEDED']])
  })

  it('stops as soon as all have ended', async () => {
    let sleeps = 0
    const ended = await waitForReferenceSyncJobs(['a'], async () => 'DEAD', { sleep: async () => { sleeps++ } })
    expect(ended.get('a')).toBe('DEAD')
    expect(sleeps).toBe(0)
  })
})

describe('Hepsiburada enum value snapshots', () => {
  it('automatically queues a read when an enum property has no current snapshot', () => {
    expect(shouldAutoSyncHepsiburadaValues({ allowsCustomValue: false, snapshotMissing: true, syncPending: false, alreadyRequested: false })).toBe(true)
  })

  it('does not request enum values for free-text or unknown property types', () => {
    expect(shouldAutoSyncHepsiburadaValues({ allowsCustomValue: true, snapshotMissing: true, syncPending: false, alreadyRequested: false })).toBe(false)
    expect(shouldAutoSyncHepsiburadaValues({ allowsCustomValue: null, snapshotMissing: true, syncPending: false, alreadyRequested: false })).toBe(false)
  })

  it('does not duplicate an active or already attempted value sync', () => {
    expect(shouldAutoSyncHepsiburadaValues({ allowsCustomValue: false, snapshotMissing: true, syncPending: true, alreadyRequested: false })).toBe(false)
    expect(shouldAutoSyncHepsiburadaValues({ allowsCustomValue: false, snapshotMissing: true, syncPending: false, alreadyRequested: true })).toBe(false)
    expect(shouldAutoSyncHepsiburadaValues({ allowsCustomValue: false, snapshotMissing: false, syncPending: false, alreadyRequested: false })).toBe(false)
  })
})

describe('reference sync job completion', () => {
  it('stops polling only for terminal outcomes', () => {
    expect(isReferenceSyncJobTerminal('PENDING')).toBe(false)
    expect(isReferenceSyncJobTerminal('LEASED')).toBe(false)
    expect(isReferenceSyncJobTerminal('RETRY_SCHEDULED')).toBe(false)
    expect(isReferenceSyncJobTerminal('SUCCEEDED')).toBe(true)
    expect(isReferenceSyncJobTerminal('BLOCKED')).toBe(true)
    expect(isReferenceSyncJobTerminal('MANUAL_REVIEW')).toBe(true)
    expect(isReferenceSyncJobTerminal('DEAD')).toBe(true)
    expect(isReferenceSyncJobTerminal('CANCELLED')).toBe(true)
  })
})
