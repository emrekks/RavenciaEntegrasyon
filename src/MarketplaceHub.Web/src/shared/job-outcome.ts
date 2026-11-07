import { hubPoll } from './api'

// What a send to a marketplace (Yayınla, Platformlarda Güncelle, Hızlı Güncelle) really ended in. Owner, 7 Nov 2026:
// RYP003T showed a success toast while its update job stopped 1.3 s later. Most of these jobs settle in seconds, so the
// screen says "Aktarım başladı…", follows the job and then says what happened: aktarıldı, aktarılamadı (with the
// reason), partly, or waiting for review. A job still running after the short wait is said to be running and its result
// still reaches the notification center; nothing reads as a success before the job says so.

export type FollowedJobDetail = {
  job: {
    id: string
    status: string
    lastErrorCode: string | null
    lastErrorSummary: string | null
    nextRetryAt?: string | null
    waiting?: boolean
  }
  variantResults?: { source: string; items: Array<{ barcode: string | null; stockCode: string | null; status: string; reason: string | null }> } | null
}

/** One job the screen follows: which marketplace and what it carries ("ürün bilgisi", "fiyat-stok", "ürün"). */
export type FollowedJob = { jobId: string; platformName: string; what: string }

export type JobOutcomeState = 'running' | 'scheduled' | 'succeeded' | 'partial' | 'failed' | 'review' | 'cancelled' | 'unknown'
export type JobOutcome = { state: JobOutcomeState; line: string }

const failedVariantStatuses = new Set(['REJECTED', 'NOT_SENT'])
const sentVariantStatuses = new Set(['SENT', 'APPROVED', 'PENDING'])

function variantName(item: { barcode: string | null; stockCode: string | null }) {
  return item.stockCode?.trim() || item.barcode?.trim() || 'bir varyant'
}

/** "RYP003T-T19: desi eksik; RYP003T-T20: …" — the first three refused variants and their reasons. */
function variantReasons(items: ReadonlyArray<{ barcode: string | null; stockCode: string | null; reason: string | null }>) {
  const shown = items.slice(0, 3).map(item => `${variantName(item)}: ${item.reason?.trim().replace(/\.$/u, '') || 'neden bildirilmedi'}`)
  return `${shown.join('; ')}${items.length > 3 ? ` ve ${items.length - 3} varyant daha` : ''}`
}

function reasonOf(job: FollowedJobDetail['job']) {
  return job.lastErrorSummary?.trim().replace(/\.$/u, '') || (job.lastErrorCode ? `hata kodu ${job.lastErrorCode}` : 'neden bildirilmedi')
}

/** The result of one job as one line of the toast, by its status and its per-variant answers. */
export function jobOutcome(followed: FollowedJob, detail: FollowedJobDetail | null | undefined): JobOutcome {
  const name = followed.platformName
  if (!detail) return { state: 'unknown', line: `${name}: ${followed.what} sonucu okunamadı; sonuç henüz bilinmiyor, İşlem Takibi'nden kontrol edin.` }
  const job = detail.job
  const status = job.status.trim().toUpperCase()
  const items = detail.variantResults?.items ?? []
  const refused = items.filter(item => failedVariantStatuses.has(item.status))
  const sent = items.filter(item => sentVariantStatuses.has(item.status))
  if (status === 'SUCCEEDED') {
    if (refused.length && sent.length) return { state: 'partial', line: `${name}: ${sent.length} varyant aktarıldı, ${refused.length} varyant aktarılamadı — ${variantReasons(refused)}.` }
    if (refused.length) return { state: 'failed', line: `${name}: aktarılamadı — ${variantReasons(refused)}.` }
    return { state: 'succeeded', line: `${name}: ${followed.what} aktarıldı.` }
  }
  if (status === 'MANUAL_REVIEW') return { state: 'review', line: `${name}: inceleme bekliyor — ${refused.length ? variantReasons(refused) : reasonOf(job)}.` }
  if (status === 'BLOCKED' || status === 'DEAD') return { state: 'failed', line: `${name}: aktarılamadı — ${refused.length ? variantReasons(refused) : reasonOf(job)}.` }
  if (status === 'CANCELLED') return { state: 'cancelled', line: `${name}: aktarım durduruldu; ${followed.what} gönderilmedi.` }
  // A retry after an error (a quota wait, a busy marketplace) is scheduled for later, not done.
  if (status === 'RETRY_SCHEDULED' && !job.waiting && job.lastErrorSummary) {
    const at = job.nextRetryAt ? new Date(job.nextRetryAt).toLocaleTimeString('tr-TR', { timeZone: 'Europe/Istanbul', hour: '2-digit', minute: '2-digit' }) : null
    return { state: 'scheduled', line: `${name}: sırada — ${reasonOf(job)}${at ? `; ${at}'de yeniden denenecek` : ''}.` }
  }
  return { state: 'running', line: `${name}: ${followed.what} aktarımı sürüyor; sonuç gelince bildirilecek.` }
}

const settledStates: ReadonlySet<JobOutcomeState> = new Set(['succeeded', 'partial', 'failed', 'review', 'cancelled'])
export const isSettledOutcome = (outcome: JobOutcome) => settledStates.has(outcome.state)

export type JobOutcomeFeedback = { title: string; kind: 'success' | 'error' | 'info'; lines: string[]; settled: boolean }

/** The toast for several followed jobs: titled by the worst one, one line each. */
export function jobOutcomeFeedback(outcomes: readonly JobOutcome[], target: string): JobOutcomeFeedback {
  const lines = outcomes.map(outcome => outcome.line)
  const has = (state: JobOutcomeState) => outcomes.some(outcome => outcome.state === state)
  const settled = outcomes.every(isSettledOutcome)
  if (has('failed') || has('partial') || has('review')) {
    const anyDone = has('succeeded') || has('partial')
    const title = has('review') && !has('failed') && !anyDone ? 'İnceleme Bekliyor' : anyDone ? 'Kısmen Aktarıldı' : 'Aktarılamadı'
    return { title, kind: 'error', lines, settled }
  }
  if (!settled) {
    const scheduled = has('scheduled') && !has('running') && !has('unknown')
    return { title: has('unknown') ? 'Sonuç Henüz Bilinmiyor' : scheduled ? 'Aktarım Sırada' : 'Aktarım Sürüyor', kind: 'info', lines, settled }
  }
  if (has('cancelled')) return { title: 'Aktarım Durduruldu', kind: 'info', lines, settled }
  return { title: `${target} Aktarıldı`, kind: 'success', lines, settled }
}

/** Every followed marketplace by name ("Trendyol", "Trendyol ve n11", "3 Platforma"). */
export function outcomeTarget(jobs: readonly FollowedJob[]) {
  const names = [...new Set(jobs.map(job => job.platformName))]
  if (names.length === 1) return dative(names[0])
  return `${names.length} Platforma`
}

// "Trendyol’a", "Hepsiburada’ya", "n11’e", "PTT AVM’ye": the suffix by the name's last vowel (a digit by how it is read).
function dative(name: string) {
  const spoken = name.replace(/11$/u, 'onbir').replace(/AVM$/u, 'aveme').replace(/ify$/iu, 'ifay')
  const lastVowel = spoken.toLocaleLowerCase('tr-TR').match(/[aeıioöuü](?=[^aeıioöuü]*$)/u)?.[0]
  if (!lastVowel) return `${name} Platformuna`
  const back = 'aıou'.includes(lastVowel)
  const buffer = /[aeıioöuü]$/u.test(spoken.toLocaleLowerCase('tr-TR')) ? 'y' : ''
  return `${name}’${buffer}${back ? 'a' : 'e'}`
}

export type FollowOptions = {
  /** Read one job; the default reads /jobs/{id}. A failed read counts as not known yet. */
  readJob?: (jobId: string) => Promise<FollowedJobDetail>
  /** How long the screen waits for the result before saying the transfer is still running. */
  shortWaitMs?: number
  /** How long the result is still followed after that, more slowly. */
  longWaitMs?: number
  pollMs?: number
  slowPollMs?: number
  sleep?: (ms: number) => Promise<void>
  now?: () => number
}

const defaultSleep = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms))
const defaultRead = (jobId: string) => hubPoll<FollowedJobDetail>(`/jobs/${jobId}`)

/**
 * Follows the jobs until all have settled. onUpdate gets the feedback once after the short wait if they still run
 * (interim: true), and once at the end (interim: false) — the settled result, or after the long wait what is known.
 */
export async function followJobOutcomes(jobs: readonly FollowedJob[], target: string, onUpdate: (feedback: JobOutcomeFeedback, interim: boolean) => void, options: FollowOptions = {}) {
  const read = options.readJob ?? defaultRead
  const sleep = options.sleep ?? defaultSleep
  const now = options.now ?? Date.now
  const shortWaitMs = options.shortWaitMs ?? 25_000
  const longWaitMs = options.longWaitMs ?? 30 * 60_000
  const started = now()
  let interimShown = false
  let outcomes: JobOutcome[] = []
  for (;;) {
    outcomes = await Promise.all(jobs.map(job => read(job.jobId).then(detail => jobOutcome(job, detail), () => jobOutcome(job, null))))
    const feedback = jobOutcomeFeedback(outcomes, target)
    const elapsed = now() - started
    if (feedback.settled) { onUpdate(feedback, false); return feedback }
    if (elapsed >= shortWaitMs + longWaitMs) {
      // Unknown stays unknown: what is still open is said to be open, never done.
      const open = outcomes.map(outcome => isSettledOutcome(outcome) ? outcome : { state: 'unknown' as const, line: outcome.line.replace(/aktarımı sürüyor; sonuç gelince bildirilecek\./u, "sonucu henüz bilinmiyor; İşlem Takibi'nden ya da ürün kartından kontrol edin.") })
      const last = jobOutcomeFeedback(open, target)
      onUpdate(last, false)
      return last
    }
    if (!interimShown && elapsed >= shortWaitMs) { interimShown = true; onUpdate(feedback, true) }
    await sleep(elapsed >= shortWaitMs ? options.slowPollMs ?? 15_000 : options.pollMs ?? 1_500)
  }
}
