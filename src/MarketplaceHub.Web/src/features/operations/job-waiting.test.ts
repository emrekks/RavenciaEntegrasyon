import { describe, expect, it } from 'vitest'
import { isWaitingJob, jobAttemptOutcome, jobAttemptResultLabel, jobAttemptsText, waitingJobLabel } from './job-waiting'

const attempt = (patch: Partial<{ completedAt: string | null; succeeded: boolean; errorCode: string | null; outcome: string }>) => ({ completedAt: '2026-10-29T11:17:02Z', succeeded: false, errorCode: null, ...patch })

describe('waiting checks', () => {
  it('shows a batch still running as a check, not a failure', () => {
    expect(jobAttemptResultLabel(attempt({ errorCode: 'PRODUCT_BATCH_PENDING', outcome: 'WAITING' }))).toBe('Kontrol Edildi · İşleniyor')
    expect(jobAttemptResultLabel(attempt({ errorCode: 'PRODUCT_APPROVAL_PENDING', outcome: 'WAITING' }))).toBe('Kontrol Edildi · Onay Bekleniyor')
  })

  it('keeps real failures, successes and runs as they are', () => {
    expect(jobAttemptResultLabel(attempt({ errorCode: 'N11_TASK_REJECTED', outcome: 'FAILED' }))).toBe('Başarısız')
    expect(jobAttemptResultLabel(attempt({ succeeded: true, outcome: 'SUCCEEDED' }))).toBe('Başarılı')
    expect(jobAttemptResultLabel(attempt({ completedAt: null, outcome: 'RUNNING' }))).toBe('Çalışıyor')
  })

  it('falls back to the stored result when the API sends no outcome', () => {
    expect(jobAttemptOutcome(attempt({ errorCode: 'PRODUCT_BATCH_PENDING' }))).toBe('FAILED')
    expect(jobAttemptOutcome(attempt({ completedAt: null }))).toBe('RUNNING')
    expect(jobAttemptOutcome(attempt({ succeeded: true }))).toBe('SUCCEEDED')
  })

  it('counts only real tries against the job budget', () => {
    // The live N11 job: five runs, four of them checks of a running batch, the fifth read the finished task.
    expect(jobAttemptsText({ status: 'SUCCEEDED', lastErrorCode: null, attemptCount: 5, maxAttempts: 10, waitingChecks: 4, attemptBudget: 10 })).toBe('1 / 10')
    expect(jobAttemptsText({ status: 'RETRY_SCHEDULED', lastErrorCode: 'PRODUCT_BATCH_PENDING', attemptCount: 3, maxAttempts: 12, waitingChecks: 2, attemptBudget: 10 })).toBe('1 / 10')
    expect(jobAttemptsText({ status: 'DEAD', lastErrorCode: 'X', attemptCount: 6, maxAttempts: 6 })).toBe('6 / 6')
  })

  it('treats only a queued job the API marks as waiting as waiting', () => {
    expect(isWaitingJob({ status: 'RETRY_SCHEDULED', waiting: true })).toBe(true)
    expect(isWaitingJob({ status: 'RETRY_SCHEDULED', waiting: false })).toBe(false)
    expect(isWaitingJob({ status: 'DEAD', waiting: true })).toBe(false)
    expect(waitingJobLabel({ lastErrorCode: 'HEPSIBURADA_PRODUCT_APPROVAL_PENDING' })).toBe('Onay Bekleniyor')
    expect(waitingJobLabel({ lastErrorCode: 'PRODUCT_BATCH_PENDING' })).toBe('İşleniyor')
  })
})
