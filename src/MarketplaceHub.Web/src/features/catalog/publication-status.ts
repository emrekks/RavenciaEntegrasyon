import { statusLabel } from '../../shared/status-labels'

const inProgressStatuses = new Set([
  'QUEUED',
  'UPDATE_QUEUED',
  'BATCH_SUBMITTED',
  'BATCH_IN_PROGRESS',
  'UPDATE_SUBMITTED',
  'UPDATE_IN_PROGRESS',
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

export function publicationStatusLabel(actualStatus?: string | null, lastJobStatus?: string | null) {
  const status = actualStatus?.trim().toUpperCase() ?? ''
  if (status && status !== 'UNKNOWN') return publicationLabels[status] ?? statusLabel(status)

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
  if (status.includes('REJECT') || status.includes('FAIL') || ['FAILED', 'BLOCKED', 'MANUAL_REVIEW'].includes(lastJobStatus?.trim().toUpperCase() ?? '')) return 'danger' as const
  if (isPublicationStatusPending(actualStatus, lastJobStatus)) return 'info' as const
  return 'neutral' as const
}
