import { statusLabel } from '../../shared/status-labels'

const inProgressStatuses = new Set([
  'QUEUED',
  'UPDATE_QUEUED',
  'BATCH_SUBMITTED',
  'BATCH_IN_PROGRESS',
  'UPDATE_SUBMITTED',
  'UPDATE_IN_PROGRESS',
  'IMPORT_SUBMITTED',
  'IMPORT_IN_PROGRESS',
  'SHOPIFY_SUBMITTED',
  'APPROVAL_PENDING',
  'APPROVAL_PARTIAL_PENDING',
  'ARCHIVE_QUEUED',
  'ARCHIVE_BATCH_SUBMITTED',
  'ARCHIVE_RECONCILING',
  'ARCHIVE_PARTIAL_PENDING',
  'UNARCHIVE_QUEUED'
])

const publicationLabels: Record<string, string> = {
  UNKNOWN: 'Henüz yayınlanmadı',
  QUEUED: 'Yayın kuyruğunda',
  UPDATE_QUEUED: 'Güncelleme kuyruğunda',
  BATCH_SUBMITTED: "Trendyol'a gönderildi",
  BATCH_IN_PROGRESS: 'Trendyol kontrol ediyor',
  CREATE_ACCEPTED: 'Trendyol kabul etti, onay bekleniyor',
  UPDATE_SUBMITTED: "Güncelleme Trendyol'a gönderildi",
  UPDATE_IN_PROGRESS: 'Trendyol güncellemeyi kontrol ediyor',
  IMPORT_SUBMITTED: "Hepsiburada'ya gönderildi",
  IMPORT_IN_PROGRESS: 'Hepsiburada kontrol ediyor',
  IMPORT_ACCEPTED: 'Hepsiburada kabul etti, inceleniyor',
  SHOPIFY_SUBMITTED: "Shopify'a gönderildi",
  UPDATE_BLOCKED: 'Güncelleme engellendi',
  APPROVAL_PENDING: 'Onay bekliyor',
  APPROVAL_PARTIAL_PENDING: 'Kısmi onay bekliyor',
  LIVE: 'Yayında',
  PARTIAL_LIVE: 'Kısmen yayında',
  REJECTED: 'Reddedildi',
  CREATE_REJECTED: 'Yayın reddedildi',
  UPDATE_REJECTED: 'Güncelleme reddedildi',
  PARTIAL_FAILURE: 'Yayın kısmen başarısız',
  PARTIAL_REJECTED: 'Bazı varyantlar reddedildi',
  UPDATE_PARTIAL_FAILURE: 'Güncelleme kısmen başarısız',
  ARCHIVE_QUEUED: 'Arşivleme kuyruğunda',
  ARCHIVE_ACCEPTED: 'Trendyol arşivlemeyi kabul etti',
  ARCHIVE_BATCH_SUBMITTED: 'Arşivleme gönderildi',
  ARCHIVE_RECONCILING: 'Arşiv durumu doğrulanıyor',
  ARCHIVE_PARTIAL_PENDING: 'Kısmi arşiv sonucu bekleniyor',
  ARCHIVE_REJECTED: 'Arşivleme reddedildi',
  ARCHIVE_PARTIAL_FAILURE: 'Arşivleme kısmen başarısız',
  ARCHIVED: 'Arşivlendi',
  UNARCHIVE_QUEUED: 'Yayına alma kuyruğunda',
  UNARCHIVE_ACCEPTED: 'Trendyol yayına almayı kabul etti',
  BLOCKED: 'İşlem engellendi',
  // PTT AVM's variant product send is a dry run for now: the request was prepared and checked, not sent.
  DRY_RUN: 'Kuru çalıştırma, gönderilmedi',
  // PTT AVM already held every variant of the family: nothing was sent, nothing failed.
  ALREADY_PRESENT: "PTT AVM'de zaten var, gönderilmedi",
  MANUAL_REVIEW: 'Elle kontrol gerekli'
}

const runningJobStatuses = new Set(['PENDING', 'RUNNING', 'LEASED', 'RETRY_SCHEDULED'])
const activelyRunningJobStatuses = new Set(['PENDING', 'RUNNING', 'LEASED'])

export function isPublicationStatusPending(actualStatus?: string | null, lastJobStatus?: string | null) {
  return inProgressStatuses.has(actualStatus?.trim().toUpperCase() ?? '')
    || runningJobStatuses.has(lastJobStatus?.trim().toUpperCase() ?? '')
}

export function isPublicationStatusJobRunning(lastJobStatus?: string | null) {
  return activelyRunningJobStatuses.has(lastJobStatus?.trim().toUpperCase() ?? '')
}

export function isPublicationLive(actualStatus?: string | null) {
  return actualStatus?.trim().toUpperCase() === 'LIVE'
}

export function shouldCheckPublicationAttributes(categoryId?: string | null, actualStatus?: string | null, lastJobStatus?: string | null) {
  return Boolean(categoryId) && !isPublicationLive(actualStatus) && !isPublicationStatusPending(actualStatus, lastJobStatus)
}

export function isPublicationSelectionDisabled(
  actualStatus: string | null | undefined,
  selected: boolean,
  statusPending: boolean,
  statusUnavailable: boolean,
  readinessBlocked: boolean
) {
  return isPublicationLive(actualStatus)
    || (!selected && (statusPending || statusUnavailable || readinessBlocked))
}

// The shared submit/check labels name Trendyol; another marketplace's card says its own name.
const platformNames: Record<string, { name: string; dative: string }> = {
  HEPSIBURADA: { name: 'Hepsiburada', dative: "Hepsiburada'ya" },
  N11: { name: 'n11', dative: "n11'e" },
  PAZARAMA: { name: 'Pazarama', dative: "Pazarama'ya" },
  PTTAVM: { name: 'PttAVM', dative: "PttAVM'ye" },
  SHOPIFY: { name: 'Shopify', dative: "Shopify'a" }
}

function forPlatform(label: string, platformCode?: string | null) {
  const platform = platformNames[platformCode?.trim().toUpperCase() ?? '']
  return platform ? label.replaceAll("Trendyol'a", platform.dative).replaceAll('Trendyol', platform.name) : label
}

export function publicationStatusLabel(actualStatus?: string | null, lastJobStatus?: string | null, platformCode?: string | null) {
  const status = actualStatus?.trim().toUpperCase() ?? ''
  if (status && status !== 'UNKNOWN') return forPlatform(publicationLabels[status] ?? statusLabel(status), platformCode)

  const jobStatus = lastJobStatus?.trim().toUpperCase() ?? ''
  if (runningJobStatuses.has(jobStatus)) return 'Yayın işi işleniyor'
  if (jobStatus === 'FAILED' || jobStatus === 'BLOCKED' || jobStatus === 'MANUAL_REVIEW') return 'Yayın işi başarısız'
  return publicationLabels.UNKNOWN
}

export function missingPublicationChecks<T extends { ok: boolean }>(checks: readonly T[]) {
  return checks.filter(check => !check.ok)
}

export function publicationStatusTone(actualStatus?: string | null, lastJobStatus?: string | null) {
  const status = actualStatus?.trim().toUpperCase() ?? ''
  if (['LIVE', 'PARTIAL_LIVE', 'APPROVED'].includes(status)) return 'success' as const
  if (status === 'DRY_RUN' || status === 'ALREADY_PRESENT') return 'info' as const
  if (status.includes('REJECT') || status.includes('FAIL') || ['FAILED', 'BLOCKED', 'MANUAL_REVIEW'].includes(lastJobStatus?.trim().toUpperCase() ?? '')) return 'danger' as const
  if (isPublicationStatusPending(actualStatus, lastJobStatus)) return 'info' as const
  return 'neutral' as const
}

export function publicationStatusNote(statusCode?: string | null) {
  // The dry run's job message, and the "already on PTT AVM" status, say what happened; their codes are no rejection.
  if (['PRODUCT_APPROVAL_CONTENT_SPLIT', 'PTTAVM_VARIANT_SEND_DRY_RUN', 'PTTAVM_VARIANTS_ALREADY_PRESENT'].includes(statusCode?.trim().toUpperCase() ?? '')) return null
  return statusCode ? `Red kodu: ${statusCode}` : null
}

// PTT AVM can accept a model family send and still not add its variants (live 2026-10-28: MZ059YNC opened with no
// variants), so the cards wait for an approval that never comes. Such a card, or one whose send stopped, may go
// again: the send reads PTT AVM first, adds only the variants it lacks and sends nothing when it holds them all. A card
// whose check is being read right now waits for that read.
const pttAvmResendableStatuses = new Set(['APPROVAL_PENDING', 'APPROVAL_PARTIAL_PENDING', 'MANUAL_REVIEW', 'BLOCKED', 'CREATE_REJECTED', 'PARTIAL_FAILURE'])

export function isPttAvmFamilyResendable(platformCode?: string | null, actualStatus?: string | null, lastJobStatus?: string | null) {
  return platformCode?.trim().toUpperCase() === 'PTTAVM'
    && pttAvmResendableStatuses.has(actualStatus?.trim().toUpperCase() ?? '')
    && !['LEASED', 'RUNNING'].includes(lastJobStatus?.trim().toUpperCase() ?? '')
}

// An approval check that is only waiting for the marketplace to approve: the card's status already says "Onay
// bekliyor", so it needs no job panel of its own. A check that failed or stopped still shows.
const approvalWaitCodes = new Set(['PRODUCT_APPROVAL_PENDING', 'HEPSIBURADA_PRODUCT_APPROVAL_PENDING'])

export function isApprovalWaitJob(job?: { jobType?: string | null; status?: string | null; lastErrorCode?: string | null } | null) {
  return job?.jobType?.trim().toUpperCase() === 'PRODUCT_APPROVAL_RECONCILE'
    && ['PENDING', 'RETRY_SCHEDULED'].includes(job.status?.trim().toUpperCase() ?? '')
    && approvalWaitCodes.has(job.lastErrorCode?.trim().toUpperCase() ?? '')
}
