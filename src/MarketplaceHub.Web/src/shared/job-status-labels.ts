// One name for each background job status (integration.jobs), wherever a job's state is shown: the İşlem Takibi
// badge and its Durum filter, the publish job window, the sync policy window's queue counts. DEAD is the job that
// used up its attempts ("Deneme Limiti Doldu"), not a generic error.
export const jobStatusLabels = {
  PENDING: 'Bekliyor',
  LEASED: 'Çalışıyor',
  RETRY_SCHEDULED: 'Yeniden Denenecek',
  BLOCKED: 'Engellendi',
  MANUAL_REVIEW: 'İnceleme Bekliyor',
  SUCCEEDED: 'Başarılı',
  DEAD: 'Deneme Limiti Doldu',
  CANCELLED: 'İptal Edildi'
} as const

export type KnownJobStatus = keyof typeof jobStatusLabels

// An unknown status is shown as it came.
export function jobStatusLabel(status: string) {
  const key = status.trim().toUpperCase()
  return key in jobStatusLabels ? jobStatusLabels[key as KnownJobStatus] : status
}
