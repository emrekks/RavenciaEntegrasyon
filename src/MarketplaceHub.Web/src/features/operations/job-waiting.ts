// A waiting check is a run that asked the marketplace about a request it had accepted and was told it is still being
// processed (a batch task, a product approval, a label). The API marks it (attempt outcome WAITING, job waiting) by
// the same code list it uses to keep such checks from using up attempts; the screens show it as a check, not a failure.

export type JobAttemptOutcome = 'SUCCEEDED' | 'FAILED' | 'WAITING' | 'RUNNING'

type AttemptLike = { completedAt: string | null; succeeded: boolean; errorCode: string | null; outcome?: string | null }
type JobLike = { status: string; lastErrorCode: string | null; attemptCount: number; maxAttempts: number; waiting?: boolean | null; waitingChecks?: number | null; attemptBudget?: number | null }

function isApprovalWait(code: string | null | undefined) {
  return /APPROVAL_PENDING$/.test(code?.trim().toUpperCase() ?? '')
}

export function jobAttemptOutcome(attempt: AttemptLike): JobAttemptOutcome {
  const outcome = attempt.outcome?.trim().toUpperCase()
  if (outcome === 'SUCCEEDED' || outcome === 'FAILED' || outcome === 'WAITING' || outcome === 'RUNNING') return outcome
  if (!attempt.completedAt) return 'RUNNING'
  return attempt.succeeded ? 'SUCCEEDED' : 'FAILED'
}

// "Kontrol Edildi · İşleniyor" for a batch still running, "Kontrol Edildi · Onay Bekleniyor" for a product in review.
export function jobAttemptResultLabel(attempt: AttemptLike) {
  const outcome = jobAttemptOutcome(attempt)
  if (outcome === 'SUCCEEDED') return 'Başarılı'
  if (outcome === 'RUNNING') return 'Çalışıyor'
  if (outcome === 'WAITING') return `Kontrol Edildi · ${isApprovalWait(attempt.errorCode) ? 'Onay Bekleniyor' : 'İşleniyor'}`
  return 'Başarısız'
}

// The job only waits for the marketplace to finish what it accepted; it has not failed.
export function isWaitingJob(job: Pick<JobLike, 'status' | 'waiting'>) {
  return Boolean(job.waiting) && (job.status === 'PENDING' || job.status === 'RETRY_SCHEDULED')
}

export function waitingJobLabel(job: Pick<JobLike, 'lastErrorCode'>) {
  return isApprovalWait(job.lastErrorCode) ? 'Onay Bekleniyor' : 'İşleniyor'
}

export function waitingJobDescription(job: Pick<JobLike, 'lastErrorCode'>) {
  return isApprovalWait(job.lastErrorCode)
    ? 'Ürün pazaryerine ulaştı ve onay bekliyor; bu bir hata değil. Onay durumu otomatik olarak yeniden kontrol edilecek.'
    : 'Pazaryeri isteği aldı ve hâlâ işliyor; bu bir hata değil. Sonuç otomatik olarak yeniden kontrol edilecek.'
}

// Tries the job made against its own budget: waiting checks are left out of both, since they did not use an attempt.
export function jobAttemptsText(job: JobLike) {
  const checks = Math.max(0, job.waitingChecks ?? 0)
  const tries = Math.max(0, job.attemptCount - checks)
  const budget = job.attemptBudget && job.attemptBudget > 0 ? job.attemptBudget : job.maxAttempts
  return `${tries} / ${budget}`
}
