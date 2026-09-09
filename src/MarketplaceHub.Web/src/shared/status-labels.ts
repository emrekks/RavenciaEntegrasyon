const statusLabels: Record<string, string> = {
  ACTIVE: 'Aktif',
  ACTION_REQUIRED: 'Aksiyon bekliyor',
  APPROVE: 'Onayla',
  APPROVED: 'Onaylandı',
  ACCEPTED: 'Kabul edildi',
  ARCHIVED: 'Arşivlendi',
  BLOCKED: 'Engellendi',
  CANCELLED: 'İptal edildi',
  CANCELLATION_PENDING: 'İptal bekliyor',
  COMPLETED: 'Tamamlandı',
  CONNECTED: 'Bağlandı',
  CREATED: 'Oluşturuldu',
  DEAD: 'Hata',
  DEGRADED: 'Yavaşlıyor',
  DELAYED: 'Gecikiyor',
  DELIVERED: 'Teslim edildi',
  DISABLED: 'Devre dışı',
  DRAFT: 'Taslak',
  FAILED: 'Hata',
  FATURA_BEKLIYOR: 'Fatura bekliyor',
  FATURA_ISLENIYOR: 'Fatura kontrol ediliyor',
  FATURA_KESILDI: 'Fatura kesildi',
  FATURA_KONTROLDE: 'Fatura kontrol ediliyor',
  FATURA_IPTAL: 'Fatura iptal edildi',
  FATURA_REDDEDILDI: 'Fatura reddedildi',
  HEALTHY: 'Sağlıklı',
  HIDDEN: 'Gizli',
  IN_PROGRESS: 'Devam ediyor',
  IN_TRANSIT: 'Taşımada',
  LEASED: 'Çalışıyor',
  MANUAL_REVIEW: 'İnceleme bekliyor',
  'DUPLICATE SAFE': 'Çoklu işleme güvenli',
  DUPLICATE_SAFE: 'Çoklu işleme güvenli',
  MIXED: 'Hata',
  NEW: 'Yeni',
  NOT_SUPPORTED: 'Desteklenmiyor',
  OFFLINE: 'Çevrim dışı',
  PARTIALLY_CANCELLED: 'Kısmi iptal',
  ON_HOLD: 'Beklemede',
  PENDING: 'Bekliyor',
  PROCESSING: 'İşleme alındı',
  READY: 'Hazır',
  READY_TO_SHIP: 'Kargoya hazır',
  REJECTED: 'Reddedildi',
  REJECT: 'Reddet',
  REQUESTED: 'Talep oluşturuldu',
  RETRY_SCHEDULED: 'Yeniden denenecek',
  RETURNED: 'İade edildi',
  RETURN_IN_TRANSIT: 'İade taşımada',
  SAFE_READ: 'Güvenli okuma',
  RUNNING: 'Çalışıyor',
  SHIPPED: 'Kargoda',
  SUCCESS: 'Tamamlandı',
  SUCCEEDED: 'Tamamlandı',
  SUSPENDED: 'Askıya alındı',
  SUPPORTED: 'Destekleniyor',
  SHIPPING: 'Kargoya verildi',
  REVIEW: 'İncelemede',
  DISPUTED: 'İhtilaflı',
  TAMAMLANDI: 'Tamamlandı',
  ONAY_BEKLIYOR: 'Onay bekliyor',
  STOK_ESLEME_GEREKLI: 'Stok eşlemesi gerekli',
  VALIDATION_FAILED: 'Doğrulama başarısız',
  UNAPPROVED: 'Onaylanmadı',
  UNDELIVERED: 'Teslim edilemedi',
  UNKNOWN: 'Bilinmiyor',
  UNKNOWN_RESULT: 'Bilinmeyen sonuç',
  VERIFIED: 'Doğrulandı',
  WAITING_FOR_SHIPMENT: 'Kargo bekliyor',
  LABEL_READ: 'Etiketi oku',
  LABEL_WRITE: 'Etiket oluşturmayı dene',
  AUTO: 'Otomatik',
  EARSIVFATURA: 'E-Arşiv fatura',
  MANUAL_UPLOAD: 'Elle yüklenen fatura belgesi',
  MARKETPLACE_DELIVERY: 'Pazaryeri teslimi',
  TEMELFATURA: 'Temel fatura'
}

function normalizedStatus(value: string) {
  return value.trim().toLocaleUpperCase('tr-TR')
}

function readableFallback(value: string) {
  return value
    .trim()
    .replace(/[_-]+/g, ' ')
    .toLocaleLowerCase('tr-TR')
    .replace(/(^|\s)\S/g, character => character.toLocaleUpperCase('tr-TR'))
}

export function statusLabel(value: string | null | undefined) {
  if (!value?.trim()) return '—'
  return statusLabels[normalizedStatus(value)] ?? readableFallback(value)
}

export function statusTone(value: string) {
  const normalized = normalizedStatus(value)
  if (['ACTIVE', 'APPROVED', 'ACCEPTED', 'COMPLETED', 'CONNECTED', 'CREATED', 'DELIVERED', 'HEALTHY', 'READY', 'SUCCESS', 'SUCCEEDED', 'SUPPORTED', 'VERIFIED'].includes(normalized)) return 'good' as const
  if (['BLOCKED', 'CANCELLATION_PENDING', 'DEAD', 'DEGRADED', 'DELAYED', 'FAILED', 'MANUAL_REVIEW', 'UNKNOWN', 'UNKNOWN_RESULT', 'UNAPPROVED'].includes(normalized)) return 'warn' as const
  return 'neutral' as const
}

export function productStatusLabel(value: string) {
  const normalized = normalizedStatus(value)
  if (normalized === 'ACTIVE') return 'Tamamlandı'
  if (normalized === 'ARCHIVED') return 'Bekliyor'
  if (normalized === 'MIXED') return 'Hata'
  return normalized === 'DRAFT' ? 'Taslak' : statusLabel(value)
}

export function productStatusTone(value: string) {
  const normalized = normalizedStatus(value)
  if (normalized === 'ACTIVE') return 'good' as const
  if (normalized === 'ARCHIVED') return 'warn' as const
  if (normalized === 'MIXED') return 'bad' as const
  return 'neutral' as const
}

export function mappingStatusLabel(value: string) {
  return ['ACTIVE', 'VERIFIED'].includes(normalizedStatus(value)) ? 'Aktif' : statusLabel(value)
}
