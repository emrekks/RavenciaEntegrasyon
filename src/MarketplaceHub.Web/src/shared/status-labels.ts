const statusLabels: Record<string, string> = {
  ACTIVE: 'Aktif',
  ACTION_REQUIRED: 'Aksiyon bekliyor',
  APPROVE: 'Onayla',
  APPROVED: 'Onaylandı',
  ACCEPTED: 'Kabul edildi',
  AWAITINGPREAPPROVAL: 'Ön onay bekliyor',
  AWAITING_PRE_APPROVAL: 'Ön onay bekliyor',
  ARCHIVED: 'Arşivlendi',
  BLOCKED: 'Engellendi',
  CANCELLED: 'İptal edildi',
  CANCELLATION_PENDING: 'İptal bekliyor',
  COMPLETED: 'Tamamlandı',
  CONFIRMATION_RETRYABLE: 'Platform doğrulaması bekleniyor',
  CONFIRMED: 'Platformda doğrulandı',
  CONNECTED: 'Bağlandı',
  CREATED: 'Oluşturuldu',
  DEAD: 'Hata',
  DEGRADED: 'Yavaşlıyor',
  DELAYED: 'Gecikiyor',
  DELIVERED: 'Teslim Edildi',
  DELİVERED: 'Teslim Edildi',
  DISABLED: 'Devre dışı',
  DRAFT: 'Taslak',
  FAILED: 'Hata',
  FATURA_BEKLIYOR: 'Fatura bekliyor',
  FATURA_ISLENIYOR: 'Fatura kontrol ediliyor',
  FATURA_KESILDI: 'Fatura kesildi',
  FATURA_YUKLENDI: 'Faturası yüklendi',
  FATURA_KONTROLDE: 'Faturası Mevcut',
  FATURA_IPTAL: 'Fatura iptal edildi',
  FATURA_REDDEDILDI: 'Fatura reddedildi',
  FATURA_BELIRSIZ: 'Fatura sonucu belirsiz',
  FATURA_DOGRULANAMADI: 'Pazaryerinden doğrulanamıyor',
  ADJUSTMENT_REQUIRED: 'Düzeltme gerekli',
  REVOCATION_REQUIRED: 'Pazaryerinden kaldırılmalı',
  HEALTHY: 'Sağlıklı',
  HIDDEN: 'Gizli',
  IN_PROGRESS: 'Devam ediyor',
  IN_TRANSIT: 'Taşımada',
  INVOICED: 'Fatura kesildi',
  INVOİCED: 'Fatura kesildi',
  LEASED: 'Çalışıyor',
  MANUAL_REVIEW: 'İnceleme bekliyor',
  'DUPLICATE SAFE': 'Çoklu işleme güvenli',
  DUPLICATE_SAFE: 'Çoklu işleme güvenli',
  MIXED: 'Hata',
  NEW: 'Yeni',
  NONE: 'Yok',
  NOT_SUPPORTED: 'Desteklenmiyor',
  NOTINVOICED: 'Fatura kesilmedi',
  NOTINVOİCED: 'Fatura kesilmedi',
  'NOT INVOICED': 'Fatura kesilmedi',
  NOT_SENT: 'Gönderilmedi',
  'NOT SENT': 'Gönderilmedi',
  OFFLINE: 'Çevrim dışı',
  PARTIALLY_CANCELLED: 'Kısmi iptal',
  PAYMENT_PENDING: 'Ödeme onayı bekliyor',
  ON_HOLD: 'Beklemede',
  PENDING: 'Bekliyor',
  PROCESSING: 'İşleme alındı',
  PREAPPROVAL_CONFIRM_SUBMITTED: 'İnceleme talebi gönderildi',
  READY: 'Hazır',
  READY_TO_SHIP: 'Kargoya hazır',
  REJECTED: 'Reddedildi',
  REJECT: 'Reddet',
  REQUESTED: 'Talep oluşturuldu',
  REMOTE_PRESENT_UNMATCHED: 'Pazaryerinde mevcut, eşleşmedi',
  'REMOTE PRESENT UNMATCHED': 'Pazaryerinde mevcut, eşleşmedi',
  RETRY_SCHEDULED: 'Yeniden denenecek',
  RETRYABLE_FAILURE: 'Yeniden denenecek',
  RETURNED: 'İade edildi',
  RETURN_IN_TRANSIT: 'İade taşımada',
  SAFE_READ: 'Güvenli okuma',
  RUNNING: 'Çalışıyor',
  SHIPPED: 'Kargoda',
  SUCCESS: 'Tamamlandı',
  SUCCEEDED: 'Tamamlandı',
  STARTED: 'Aktarım başlatıldı',
  SUBMITTED: 'Platforma gönderildi',
  SUSPENDED: 'Askıya alındı',
  SUPPORTED: 'Destekleniyor',
  SHIPPING: 'Kargoya verildi',
  REVIEW: 'İncelemede',
  DISPUTED: 'İhtilaflı',
  TAMAMLANDI: 'Tamamlandı',
  ONAY_BEKLIYOR: 'Onay bekliyor',
  STOK_ESLEME_GEREKLI: 'Stok eşlemesi gerekli',
  VALIDATION_FAILED: 'Doğrulama başarısız',
  MARKETPLACE_PENDING: 'Pazaryerine aktarılıyor',
  MARKETPLACE_FAILED: 'Pazaryeri aktarımı başarısız',
  FATURA_PLATFORMA_AKTARILMADI: 'Fatura oluşturuldu, platforma aktarılmadı',
  FATURA_PLATFORMA_BEKLIYOR: 'Platforma gönderilmeyi bekliyor',
  UNAPPROVED: 'Onaylanmadı',
  UNDELIVERED: 'Teslim edilemedi',
  UNVERIFIED: 'Durumu doğrulanmadı',
  UNKNOWN: 'Bilinmiyor',
  UNKNOWN_RESULT: 'Bilinmeyen sonuç',
  VERIFIED: 'Doğrulandı',
  WAITING_FOR_SHIPMENT: 'Kargo bekliyor',
  WAITINGINACTION: 'Trendyol’da karar bekliyor',
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

export type InvoiceStatusTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger'

export function invoiceStatusLabel(value: string | null | undefined) {
  const normalized = value?.trim().toLocaleUpperCase('tr-TR') ?? ''
  if (['FATURA_BEKLIYOR', 'PENDING', 'DRAFT'].includes(normalized)) return 'Fatura Bekliyor'
  if (['FATURA_KONTROLDE', 'FATURA_YUKLENDI', 'FATURA_KESILDI', 'COMPLETED'].includes(normalized)) return 'Faturası Mevcut'
  if (['FATURA_ISLENIYOR', 'FATURA_PLATFORMA_BEKLIYOR', 'PROCESSING', 'SUBMITTING', 'SUBMITTED', 'MARKETPLACE_PENDING', 'READY', 'ACCEPTED'].includes(normalized)) return 'Fatura İletiliyor'
  if (['FATURA_PLATFORMA_AKTARILMADI', 'MARKETPLACE_FAILED'].includes(normalized)) return 'Fatura İletilemedi'
  // A lost provider answer is not a rejection: the invoice may exist at the provider until it is checked.
  if (['FATURA_BELIRSIZ', 'UNKNOWN_RESULT'].includes(normalized)) return 'Fatura Sonucu Belirsiz'
  if (['ADJUSTMENT_REQUIRED', 'FATURA_DUZELTME_GEREKLI'].includes(normalized)) return 'Fatura Düzeltme Gerekli'
  if (['FATURA_REDDEDILDI', 'REJECTED', 'VALIDATION_FAILED', 'MANUAL_REVIEW'].includes(normalized)) return 'Fatura Oluşturulamadı'
  if (['FATURA_IPTAL', 'CANCELLED', 'CANCELLED_LOCAL'].includes(normalized)) return 'Fatura İptal'
  // n11 / PTT AVM do not show an invoice uploaded in their seller panel; the seller marks it by hand.
  if (normalized === 'FATURA_DOGRULANAMADI') return 'Fatura Doğrulanamadı'
  return 'Fatura durumu bilinmiyor'
}

export function invoiceStatusDisplayLabel(value: string | null | undefined, isMicroExport = false) {
  const label = invoiceStatusLabel(value)
  return isMicroExport ? label.replace(/^Fatura\s/u, 'Mikro İhracat Faturası ') : label
}

export function invoiceStatusTone(value: string | null | undefined): InvoiceStatusTone {
  const normalized = value?.trim().toLocaleUpperCase('tr-TR') ?? ''
  if (['FATURA_BEKLIYOR', 'PENDING', 'DRAFT'].includes(normalized)) return 'warning'
  if (['FATURA_ISLENIYOR', 'FATURA_PLATFORMA_BEKLIYOR', 'PROCESSING', 'SUBMITTING', 'SUBMITTED', 'MARKETPLACE_PENDING', 'READY', 'ACCEPTED'].includes(normalized)) return 'info'
  if (['FATURA_BELIRSIZ', 'UNKNOWN_RESULT', 'ADJUSTMENT_REQUIRED', 'FATURA_DUZELTME_GEREKLI', 'FATURA_DOGRULANAMADI'].includes(normalized)) return 'warning'
  if (['FATURA_REDDEDILDI', 'REJECTED', 'VALIDATION_FAILED', 'MARKETPLACE_FAILED', 'MANUAL_REVIEW', 'FATURA_PLATFORMA_AKTARILMADI'].includes(normalized)) return 'danger'
  if (['FATURA_IPTAL', 'CANCELLED', 'CANCELLED_LOCAL'].includes(normalized)) return 'neutral'
  if (['FATURA_YUKLENDI', 'FATURA_KESILDI', 'FATURA_KONTROLDE', 'COMPLETED'].includes(normalized)) return 'success'
  return 'neutral'
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
  if (['AWAITINGPREAPPROVAL', 'AWAITING_PRE_APPROVAL', 'BLOCKED', 'CANCELLATION_PENDING', 'DEAD', 'DEGRADED', 'DELAYED', 'FAILED', 'MANUAL_REVIEW', 'PAYMENT_PENDING', 'UNKNOWN', 'UNKNOWN_RESULT', 'UNAPPROVED', 'UNVERIFIED'].includes(normalized)) return 'warn' as const
  return 'neutral' as const
}

export function productStatusLabel(value: string) {
  const normalized = normalizedStatus(value)
  if (normalized === 'ACTIVE') return 'Satışa Açık'
  if (normalized === 'ARCHIVED') return 'Satışa Kapalı'
  if (normalized === 'MIXED') return 'Hata'
  return normalized === 'DRAFT' ? 'Taslak' : statusLabel(value)
}

export function mappingStatusLabel(value: string) {
  return ['ACTIVE', 'VERIFIED'].includes(normalizedStatus(value)) ? 'Aktif' : statusLabel(value)
}
