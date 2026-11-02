// A publish card's "Yayın Durumu" as four steps: Hazırlık (the product and its mappings), Gönderim (the send),
// Onay (the marketplace's check) and Yayında (live there). Each step is one of:
// done (green check), next (dashed green: ready, waiting to be sent), fail (red x: blocked or refused at that step),
// now (blue spinner: a job running or an approval awaited), half (Yayında only: partly live, a ring of the live share)
// and idle (grey). The card's counts ride beside the steps: the live share, the required gaps (or the variants the
// marketplace no longer knows), the optional warnings. An unknown count is left out, never shown as 0.

export type PublishStepState = 'done' | 'next' | 'fail' | 'now' | 'half' | 'idle'
export type PublishStepLink = 'full' | 'half' | 'empty'

export const publishStepNames = ['Hazırlık', 'Gönderim', 'Onay', 'Yayında'] as const

export type PublishStepsInput = {
  readOnly?: boolean
  /** The card is still reading what it rests on: every step grey, under the card's mask. */
  settling?: boolean
  statusUnavailable?: boolean
  actualStatus?: string | null
  lastJobStatus?: string | null
  /** Required gaps that block the send (0 when none). */
  requiredGaps: number
  optionalWarnings: number
  /** Every variant is live and none is missing. */
  live: boolean
  /** Partly live: how many of how many (variants, or colour cards of the family). */
  partial?: { live: number; total: number; unit: 'varyant' | 'renk' } | null
  /** Nothing the card once had is known to the marketplace any more. */
  notFound?: boolean
  /** Variants the marketplace no longer knows (not found or refused), 0 when none or not known. */
  missingVariants?: number
  /** PTT AVM family to send again ("Tekrar Deneyin"). */
  retry?: boolean
  /** PTT AVM has no approval: its wait is our own check. */
  checking?: boolean
  /** The card's last publication job: running, or settled with a failure at one step (the send, the approval check;
   *  null for a status check, whose failure says nothing about the listing). */
  job?: { running: boolean; failed: boolean; step: 'send' | 'approval' | null }
  /** Words for a state the steps do not name themselves (archived, a dry run, "Ürün Yayını Desteklenmiyor"). */
  fallbackLabel: string
}

export type PublishSteps = {
  steps: [PublishStepState, PublishStepState, PublishStepState, PublishStepState]
  links: [PublishStepLink, PublishStepLink, PublishStepLink]
  /** The live share for the half ring, 0–100; null when it is not known. */
  share: number | null
  label: string
  counts: { partial?: string; required?: number; missing?: { count: number; word: 'ret' | 'yok' }; optional?: number }
}

const sendingStatuses = new Set(['QUEUED', 'UPDATE_QUEUED'])
// Sent and waiting for the marketplace's word (CREATE_RESULT_UNKNOWN: the batch result had no line for the variant yet,
// the approval check reads it by barcode).
const approvalStatuses = new Set(['BATCH_SUBMITTED', 'BATCH_IN_PROGRESS', 'CREATE_RESULT_UNKNOWN', 'CREATE_ACCEPTED', 'UPDATE_SUBMITTED', 'UPDATE_IN_PROGRESS', 'IMPORT_SUBMITTED', 'IMPORT_IN_PROGRESS', 'IMPORT_ACCEPTED', 'SHOPIFY_SUBMITTED', 'APPROVAL_PENDING', 'APPROVAL_PARTIAL_PENDING'])
const rejectedStatuses = new Set(['REJECTED', 'CREATE_REJECTED', 'UPDATE_REJECTED', 'MANUAL_REVIEW'])
const partlyFailedStatuses = new Set(['PARTIAL_FAILURE', 'UPDATE_PARTIAL_FAILURE'])
const blockedStatuses = new Set(['BLOCKED', 'UPDATE_BLOCKED'])
const archivingStatuses = new Set(['ARCHIVE_QUEUED', 'ARCHIVE_ACCEPTED', 'ARCHIVE_BATCH_SUBMITTED', 'ARCHIVE_RECONCILING', 'ARCHIVE_PARTIAL_PENDING', 'UNARCHIVE_QUEUED', 'UNARCHIVE_ACCEPTED'])
const failedJobStatuses = new Set(['FAILED', 'DEAD', 'BLOCKED', 'MANUAL_REVIEW'])

type Four = PublishSteps['steps']

function linksOf(steps: Four): PublishSteps['links'] {
  const link = (prev: PublishStepState, next: PublishStepState): PublishStepLink =>
    prev !== 'done' ? 'empty' : next === 'next' ? 'half' : next === 'idle' ? 'empty' : 'full'
  return [link(steps[0], steps[1]), link(steps[1], steps[2]), link(steps[2], steps[3])]
}

export function publishStatusSteps(input: PublishStepsInput): PublishSteps {
  const status = input.actualStatus?.trim().toUpperCase() ?? ''
  const jobStatus = input.lastJobStatus?.trim().toUpperCase() ?? ''
  const partial = input.partial && input.partial.total > 0 ? input.partial : null
  const share = partial ? Math.round(Math.max(0, Math.min(1, partial.live / partial.total)) * 100) : null
  const build = (steps: Four, label: string, knownShare: number | null = share): PublishSteps => {
    const counts: PublishSteps['counts'] = {}
    if (partial) counts.partial = `${partial.live}/${partial.total}`
    if (input.requiredGaps > 0) counts.required = input.requiredGaps
    else if ((input.missingVariants ?? 0) > 0 && steps[3] !== 'done') counts.missing = { count: input.missingVariants!, word: rejectedStatuses.has(status) || status === 'PARTIAL_REJECTED' ? 'ret' : 'yok' }
    if (input.optionalWarnings > 0 && steps[3] !== 'done') counts.optional = input.optionalWarnings
    return { steps, links: linksOf(steps), share: steps[3] === 'half' ? knownShare : null, label, counts }
  }
  const idle: Four = ['idle', 'idle', 'idle', 'idle']
  if (input.settling) return { steps: idle, links: linksOf(idle), share: null, label: 'Kontrol Ediliyor', counts: {} }
  if (input.readOnly) return { steps: idle, links: linksOf(idle), share: null, label: input.fallbackLabel, counts: {} }
  if (input.statusUnavailable) return { steps: idle, links: linksOf(idle), share: null, label: 'Durum Alınamadı', counts: {} }

  const prepFailed = input.requiredGaps > 0
  const jobRunning = Boolean(input.job?.running)
  if (input.live) return build(['done', 'done', 'done', 'done'], 'Yayında')
  if (input.notFound) return build(['done', 'done', 'done', 'fail'], 'Bulunamadı')

  if (partial) {
    if (status === 'PARTIAL_REJECTED') return build(['done', 'done', 'fail', 'half'], 'Kısmen Reddedildi')
    if (jobRunning && !approvalStatuses.has(status)) return build(['done', 'now', 'idle', 'half'], 'Gönderiliyor')
    if (approvalStatuses.has(status) && !input.retry) return build(['done', 'done', 'now', 'half'], input.checking ? 'Kontrol Ediliyor' : 'Onay Bekliyor')
    if (prepFailed) return build(['fail', 'idle', 'idle', 'half'], 'Kısmen Yayında')
    return build(['done', 'next', 'idle', 'half'], 'Kısmen Yayında')
  }

  if (input.retry) return build(['done', 'next', 'idle', 'idle'], 'Tekrar Deneyin')
  if (approvalStatuses.has(status)) return build(['done', 'done', 'now', 'idle'], input.checking ? 'Kontrol Ediliyor' : 'Onay Bekliyor')
  if (sendingStatuses.has(status) || (jobRunning && (!status || status === 'UNKNOWN'))) return build(['done', 'now', 'idle', 'idle'], 'Gönderiliyor')
  if (status === 'LIVE') return build(['done', 'done', 'done', 'done'], 'Yayında')
  if (status === 'PARTIAL_LIVE') return build(['done', 'done', 'done', 'half'], 'Kısmen Yayında')
  if (archivingStatuses.has(status)) return build(['done', 'done', 'done', 'now'], input.fallbackLabel)
  if (status === 'ARCHIVED') return build(['done', 'done', 'done', 'idle'], input.fallbackLabel)
  if (status === 'ARCHIVE_REJECTED' || status === 'ARCHIVE_PARTIAL_FAILURE') return build(['done', 'done', 'done', 'fail'], input.fallbackLabel)
  // A required gap is what the user can act on: it leads, also over an earlier refusal.
  if (prepFailed) return build(['fail', 'idle', 'idle', 'idle'], 'Zorunlu Eksik')
  if (status === 'PARTIAL_REJECTED') return build(['done', 'done', 'fail', 'half'], 'Kısmen Reddedildi')
  if (rejectedStatuses.has(status)) return build(['done', 'done', 'fail', 'idle'], 'Reddedildi')
  if (partlyFailedStatuses.has(status)) return build(['done', 'fail', 'idle', 'half'], 'Kısmen Başarısız')
  if (blockedStatuses.has(status)) return build(['done', 'fail', 'idle', 'idle'], 'Gönderilemedi')
  const failedStep = input.job?.failed ? input.job.step : failedJobStatuses.has(jobStatus) ? 'send' : null
  if (failedStep === 'approval') return build(['done', 'done', 'fail', 'idle'], 'Onay Alınamadı')
  if (failedStep === 'send') return build(['done', 'fail', 'idle', 'idle'], 'Gönderilemedi')
  if (status === 'DRY_RUN') return build(['done', 'next', 'idle', 'idle'], input.fallbackLabel)
  if (status === 'ALREADY_PRESENT') return build(['done', 'done', 'idle', 'idle'], input.fallbackLabel)
  return build(['done', 'next', 'idle', 'idle'], 'Yayına Hazır')
}

const stateWords: Record<PublishStepState, string> = { done: 'Tamam', next: 'Sırada', fail: 'Takıldı', now: 'Sürüyor', half: 'Kısmen', idle: 'Bekliyor' }

export function publishStepTitle(index: number, state: PublishStepState, steps: PublishSteps) {
  const name = publishStepNames[index]
  if (state === 'half' && steps.counts.partial) return `${name} · ${steps.counts.partial}`
  return `${name} · ${stateWords[state]}`
}

/** The pill's one accessible name: "Trendyol: Kısmen Yayında, 21/42 varyant yayında; 1 zorunlu eksik, 2 uyarı". */
export function publishStepsAriaLabel(platformName: string, steps: PublishSteps, unit: 'varyant' | 'renk' = 'varyant') {
  const { partial, required, missing, optional } = steps.counts
  const notes = [
    required ? `${required} zorunlu eksik` : '',
    missing ? `${missing.count} varyant ${missing.word === 'ret' ? 'reddedildi' : 'pazaryerinde bulunamadı'}` : '',
    optional ? `${optional} uyarı` : ''
  ].filter(Boolean)
  return `${platformName}: ${steps.label}${partial ? `, ${partial} ${unit} yayında` : ''}${notes.length ? `; ${notes.join(', ')}` : ''}`
}

// The counts sit as corner badges on the step they concern: the red one (required gaps, or the variants refused or no
// longer found) on the failing step, the yellow one (optional warnings) on Hazırlık, the green one (how many are live)
// on a partly live Yayında. A step with two badges carries the second at its lower corner.
export type PublishStepBadge = { step: number; tone: 'red' | 'yellow' | 'green'; text: string; corner: 'top' | 'bottom'; title: string }

export function publishStepBadges(steps: PublishSteps, unit: 'varyant' | 'renk' = 'varyant'): PublishStepBadge[] {
  const { partial, required, missing, optional } = steps.counts
  const failing = steps.steps.indexOf('fail')
  const badges: Array<Omit<PublishStepBadge, 'corner'>> = []
  if (required) badges.push({ step: failing >= 0 ? failing : 0, tone: 'red', text: String(required), title: `${required} zorunlu eksik` })
  else if (missing) badges.push({ step: failing >= 0 ? failing : 3, tone: 'red', text: String(missing.count), title: `${missing.count} varyant ${missing.word === 'ret' ? 'reddedildi' : 'pazaryerinde bulunamadı'}` })
  if (partial) badges.push({ step: 3, tone: 'green', text: partial.split('/')[0], title: `${partial} ${unit} yayında` })
  if (optional) badges.push({ step: 0, tone: 'yellow', text: String(optional), title: `${optional} isteğe bağlı uyarı` })
  return badges.map((badge, index) => ({ ...badge, corner: badges.slice(0, index).some(other => other.step === badge.step) ? 'bottom' : 'top' }))
}

// The pill's state label shows only when the cell is wide enough for it; the cell's container queries hide it below the
// width this estimate rounds up to, in 20 px steps (publish-platform-issues.css). The steps and their badges always show.
export const publishStepsFitTiers = [180, 200, 220, 240, 260, 280, 300, 320, 340, 360] as const

export function publishStepsFit(steps: PublishSteps) {
  // Measured live: the track is 124 px, the gap 12, padding and border 20; a 12.5 px bold letter about 6.3 px.
  const width = 156 + steps.label.length * 6.6
  return publishStepsFitTiers.find(tier => tier >= width) ?? 400
}
