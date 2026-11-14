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

// A long job that read one window and saved its position (RETRY_SCHEDULED with SLICE_CONTINUES) runs again in seconds:
// it is not retrying and has not failed. The server counts it apart as CONTINUING.
export const sliceContinuesCode = 'SLICE_CONTINUES'
export function isContinuingJob(job: Pick<JobLike, 'status' | 'lastErrorCode'>) {
  return job.status === 'RETRY_SCHEDULED' && job.lastErrorCode?.trim().toUpperCase() === sliceContinuesCode
}

// A job deferred on purpose, with its attempt given back (the connection is busy with another job, an earlier read is
// pending, the platform's request quota is used up): it is waiting its turn, not retrying after an error.
export type QueuedWait = { label: string; line: string; description: string }
const turnWait: Omit<QueuedWait, 'label'> = {
  line: 'aynı bağlantıdaki işlem bitince başlayacak',
  description: 'Bu bağlantıda aynı türde başka bir işlem sürüyor; işlem tamamlanınca otomatik çalıştırılacak. Bu bir hata değil.'
}
const turnCodes = new Set(['CONNECTION_OPERATION_BUSY', 'ORDER_INVOICE_RECONCILIATION_BUSY', 'TARGETED_ORDER_SYNC_BUSY', 'CONNECTION_FULL_RESET_BUSY', 'PANEL_RESET_BUSY', 'ORDER_READ_PENDING', 'PRODUCT_FAMILY_ORDER_WAIT'])
export function queuedWait(job: Pick<JobLike, 'status' | 'lastErrorCode'>): QueuedWait | null {
  if (job.status !== 'PENDING' && job.status !== 'RETRY_SCHEDULED') return null
  const code = job.lastErrorCode?.trim().toUpperCase() ?? ''
  if (turnCodes.has(code)) return { label: 'Sırada', ...turnWait }
  if (code === 'INITIAL_SYNC_PENDING') return { label: 'Sırada', line: 'ilk veri aktarımı bitince başlayacak', description: 'İlk kapsamlı veri aktarımı tamamlanmadan bu işlem başlamaz; aktarım bitince otomatik çalıştırılacak. Bu bir hata değil.' }
  if (code === 'LOCAL_QUOTA_WAIT' || code.endsWith('_LOCAL_QUOTA_WAIT')) return { label: 'Kota Bekleniyor', line: 'platform istek kotası dolunca yeniden denenecek', description: 'Platformun istek kotası dolu olduğu için istek gönderilmedi; kota yenilenince otomatik çalıştırılacak. Bu bir hata değil.' }
  return null
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
// A status check that follows a marketplace for days (an approval: one check every few minutes up to 7 days) carries
// a budget in the thousands that is a time limit, not a try count; "1 / 2017" said nothing, so only the tries show.
export function jobAttemptsText(job: JobLike) {
  const checks = Math.max(0, job.waitingChecks ?? 0)
  const tries = Math.max(0, job.attemptCount - checks)
  const budget = job.attemptBudget && job.attemptBudget > 0 ? job.attemptBudget : job.maxAttempts
  return budget > maxShownAttemptBudget ? `${tries}` : `${tries} / ${budget}`
}

const maxShownAttemptBudget = 99
