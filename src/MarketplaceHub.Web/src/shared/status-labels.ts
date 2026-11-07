const statusLabels: Record<string, string> = {
  ACTIVE: 'Aktif',
  ACTION_REQUIRED: 'Aksiyon Bekliyor',
  APPROVE: 'Onayla',
  APPROVED: 'Onaylandı',
  ACCEPTED: 'Kabul Edildi',
  AWAITINGPREAPPROVAL: 'Ön Onay Bekliyor',
  AWAITING_PRE_APPROVAL: 'Ön Onay Bekliyor',
  ARCHIVED: 'Arşivlendi',
  BLOCKED: 'Engellendi',
  CANCELLED: 'İptal Edildi',
  CANCELLATION_PENDING: 'İptal Bekliyor',
  CANCELLATION_REJECTED: 'İptal Reddedildi',
  COMPLETED: 'Tamamlandı',
  CONFIRMATION_RETRYABLE: 'Platform Doğrulaması Bekleniyor',
  CONFIRMED: 'Platformda Doğrulandı',
  CONNECTED: 'Bağlandı',
  CREATED: 'Oluşturuldu',
  CREATE_RESULT_UNKNOWN: 'Sonuç Bekleniyor',
  DEAD: 'Hata',
  DEGRADED: 'Yavaşlıyor',
  DELAYED: 'Gecikiyor',
  DELIVERED: 'Teslim Edildi',
  DELİVERED: 'Teslim Edildi',
  DISABLED: 'Devre Dışı',
  DRAFT: 'Taslak',
  FAILED: 'Hata',
  FATURA_BEKLIYOR: 'Fatura Bekliyor',
  FATURA_ISLENIYOR: 'Fatura Kontrol Ediliyor',
  FATURA_KESILDI: 'Fatura Kesildi',
  FATURA_YUKLENDI: 'Faturası Yüklendi',
  FATURA_KONTROLDE: 'Faturası Mevcut',
  FATURA_IPTAL: 'Fatura İptal Edildi',
  FATURA_REDDEDILDI: 'Fatura Reddedildi',
  FATURA_BELIRSIZ: 'Fatura Sonucu Belirsiz',
  FATURA_DOGRULANAMADI: 'Pazaryerinden Doğrulanamıyor',
  FATURA_INCELENECEK: 'İncelenecek',
  FATURA_INCELEME_GEREKLI: 'İnceleme Gerekli',
  ADJUSTMENT_REQUIRED: 'Düzeltme Gerekli',
  REVOCATION_REQUIRED: 'Pazaryerinden Kaldırılmalı',
  HEALTHY: 'Sağlıklı',
  HIDDEN: 'Gizli',
  IN_PROGRESS: 'Devam Ediyor',
  IN_TRANSIT: 'Taşımada',
  INVOICED: 'Fatura Kesildi',
  INVOİCED: 'Fatura kesildi',
  LEASED: 'Çalışıyor',
  MANUAL_REVIEW: 'İnceleme Bekliyor',
  'DUPLICATE SAFE': 'Çoklu İşleme Güvenli',
  DUPLICATE_SAFE: 'Çoklu İşleme Güvenli',
  MIXED: 'Hata',
  NEW: 'Yeni',
  NONE: 'Yok',
  NOT_SUPPORTED: 'Desteklenmiyor',
  NOTINVOICED: 'Fatura Kesilmedi',
  NOTINVOİCED: 'Fatura kesilmedi',
  'NOT INVOICED': 'Fatura Kesilmedi',
  NOT_SENT: 'Gönderilmedi',
  'NOT SENT': 'Gönderilmedi',
  OFFLINE: 'Çevrim Dışı',
  PARTIALLY_CANCELLED: 'Kısmi İptal',
  // A return whose lines were decided both ways (one approved, another rejected): ReturnClaimPartialApprovalPolicy.
  PARTIALLY_APPROVED: 'Kısmen Onaylandı',
  PAYMENT_PENDING: 'Ödeme Onayı Bekliyor',
  ON_HOLD: 'Beklemede',
  PENDING: 'Bekliyor',
  PROCESSING: 'İşleme Alındı',
  PREAPPROVAL_CONFIRM_SUBMITTED: 'İnceleme Talebi Gönderildi',
  READY: 'Hazır',
  READY_TO_SHIP: 'Kargoya Hazır',
  REJECTED: 'Reddedildi',
  REJECT: 'Reddet',
  REQUESTED: 'Talep Oluşturuldu',
  REMOTE_PRESENT_UNMATCHED: 'Pazaryerinde Mevcut, Eşleşmedi',
  'REMOTE PRESENT UNMATCHED': 'Pazaryerinde Mevcut, Eşleşmedi',
  RETRY_SCHEDULED: 'Yeniden Denenecek',
  RETRYABLE_FAILURE: 'Yeniden Denenecek',
  RETURNED: 'İade Edildi',
  RETURN_IN_TRANSIT: 'İade Taşımada',
  SAFE_READ: 'Güvenli Okuma',
  RUNNING: 'Çalışıyor',
  SHIPPED: 'Kargoda',
  SUCCESS: 'Tamamlandı',
  SUCCEEDED: 'Tamamlandı',
  STARTED: 'Aktarım Başlatıldı',
  SUBMITTED: 'Platforma Gönderildi',
  SUSPENDED: 'Askıya Alındı',
  SUPPORTED: 'Destekleniyor',
  SHIPPING: 'Kargoya Verildi',
  REVIEW: 'İncelemede',
  DISPUTED: 'İhtilaflı',
  TAMAMLANDI: 'Tamamlandı',
  ONAY_BEKLIYOR: 'Onay Bekliyor',
  STOK_ESLEME_GEREKLI: 'Stok Eşlemesi Gerekli',
  VALIDATION_FAILED: 'Doğrulama Başarısız',
  MARKETPLACE_PENDING: 'Pazaryerine Aktarılıyor',
  MARKETPLACE_FAILED: 'Pazaryeri Aktarımı Başarısız',
  FATURA_PLATFORMA_AKTARILMADI: 'Fatura Oluşturuldu, Platforma Aktarılmadı',
  FATURA_PLATFORMA_BEKLIYOR: 'Platforma Gönderilmeyi Bekliyor',
  UNAPPROVED: 'Onaylanmadı',
  UNDELIVERED: 'Teslim Edilemedi',
  UNVERIFIED: 'Durumu Doğrulanmadı',
  UNKNOWN: 'Bilinmiyor',
  UNKNOWN_RESULT: 'Bilinmeyen Sonuç',
  VERIFIED: 'Doğrulandı',
  WAITING_FOR_SHIPMENT: 'Kargo Bekliyor',
  WAITINGINACTION: 'Pazaryerinde Karar Bekliyor',
  LABEL_READ: 'Etiketi Oku',
  LABEL_WRITE: 'Etiket Oluşturmayı Dene',
  AUTO: 'Otomatik',
  EARSIVFATURA: 'E-Arşiv Fatura',
  MANUAL_UPLOAD: 'Elle Yüklenen Fatura Belgesi',
  MARKETPLACE_DELIVERY: 'Pazaryeri Teslimi',
  TEMELFATURA: 'Temel Fatura'
}

// Status keys are ASCII codes (WAITINGINACTION, FATURA_IPTAL): plain upper case, so a raw lower-case word's "i" becomes
// "I" and finds its key, where Turkish casing made it "İ" and missed it.
function normalizedStatus(value: string) {
  return value.trim().toUpperCase()
}

export type InvoiceStatusTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger'

// The workspace label of a package whose order came back in full before it was invoiced (InvoicingBillingService.FullyReturnedNoInvoiceLabel).
export const fullyReturnedNoInvoiceStatus = 'IADE_EDILDI_FATURA_KESILMEZ'

export function invoiceStatusLabel(value: string | null | undefined) {
  const normalized = value?.trim().toUpperCase() ?? ''
  // An N11 / PttAVM / Pazarama state the marketplace cannot verify waits for an invoice like any not-invoiced one.
  if (['FATURA_BEKLIYOR', 'FATURA_DOGRULANAMADI', 'PENDING', 'DRAFT'].includes(normalized)) return 'Fatura Bekliyor'
  if (['FATURA_KONTROLDE', 'FATURA_YUKLENDI', 'FATURA_KESILDI', 'COMPLETED'].includes(normalized)) return 'Faturası Mevcut'
  if (['FATURA_ISLENIYOR', 'FATURA_PLATFORMA_BEKLIYOR', 'PROCESSING', 'SUBMITTING', 'SUBMITTED', 'MARKETPLACE_PENDING', 'READY', 'ACCEPTED'].includes(normalized)) return 'Fatura İletiliyor'
  if (['FATURA_PLATFORMA_AKTARILMADI', 'MARKETPLACE_FAILED'].includes(normalized)) return 'Fatura İletilemedi'
  // A lost provider answer is not a rejection: the invoice may exist at the provider until it is checked.
  if (['FATURA_BELIRSIZ', 'UNKNOWN_RESULT'].includes(normalized)) return 'Fatura Sonucu Belirsiz'
  // E-Faturam took the request but its answer was unreadable; the result is read from E-Faturam.
  if (normalized === 'FATURA_CEVAP_BEKLENIYOR') return 'E-Faturam Cevap Bekleniyor'
  // A Shopify order without a panel invoice or our invoice link: nothing says whether it was invoiced elsewhere.
  if (normalized === 'FATURA_INCELENECEK') return 'İncelenecek'
  // Issued at E-Faturam and stopped for review (the marketplace may already show an invoice): not a refusal.
  if (normalized === 'FATURA_INCELEME_GEREKLI') return 'İnceleme Gerekli'
  if (['ADJUSTMENT_REQUIRED', 'FATURA_DUZELTME_GEREKLI'].includes(normalized)) return 'Fatura Düzeltme Gerekli'
  // Every unit came back before an invoice was issued: no sale invoice is due (owner's rule of 6 Nov 2026).
  if (normalized === fullyReturnedNoInvoiceStatus) return 'İade Edildi, Fatura Kesilmez'
  if (['FATURA_REDDEDILDI', 'REJECTED', 'VALIDATION_FAILED', 'MANUAL_REVIEW'].includes(normalized)) return 'Fatura Oluşturulamadı'
  if (['FATURA_IPTAL', 'CANCELLED', 'CANCELLED_LOCAL'].includes(normalized)) return 'Fatura İptal'
  return 'Fatura durumu bilinmiyor'
}

export function invoiceStatusDisplayLabel(value: string | null | undefined, isMicroExport = false) {
  const label = invoiceStatusLabel(value)
  return isMicroExport ? label.replace(/^Fatura\s/u, 'İhracat Faturası ') : label
}

export function invoiceStatusTone(value: string | null | undefined): InvoiceStatusTone {
  const normalized = value?.trim().toUpperCase() ?? ''
  if (['FATURA_BEKLIYOR', 'PENDING', 'DRAFT'].includes(normalized)) return 'warning'
  if (['FATURA_ISLENIYOR', 'FATURA_PLATFORMA_BEKLIYOR', 'PROCESSING', 'SUBMITTING', 'SUBMITTED', 'MARKETPLACE_PENDING', 'READY', 'ACCEPTED'].includes(normalized)) return 'info'
  if (['FATURA_BELIRSIZ', 'UNKNOWN_RESULT', 'FATURA_CEVAP_BEKLENIYOR', 'ADJUSTMENT_REQUIRED', 'FATURA_DUZELTME_GEREKLI', 'FATURA_DOGRULANAMADI', 'FATURA_INCELENECEK', 'FATURA_INCELEME_GEREKLI'].includes(normalized)) return 'warning'
  if (['FATURA_REDDEDILDI', 'REJECTED', 'VALIDATION_FAILED', 'MARKETPLACE_FAILED', 'MANUAL_REVIEW', 'FATURA_PLATFORMA_AKTARILMADI'].includes(normalized)) return 'danger'
  if (['FATURA_IPTAL', 'CANCELLED', 'CANCELLED_LOCAL'].includes(normalized)) return 'neutral'
  if (['FATURA_YUKLENDI', 'FATURA_KESILDI', 'FATURA_KONTROLDE', 'COMPLETED'].includes(normalized)) return 'success'
  return 'neutral'
}

// A code without a Turkish label is an English enum (IMPORT_SUBMITTED): cased in English it reads "Import Submitted";
// Turkish casing turned its I into ı ("Submıtted"). Text with Turkish letters keeps Turkish casing.
function readableFallback(value: string) {
  const locale = /^[\x20-\x7e]*$/.test(value) ? 'en-US' : 'tr-TR'
  return value
    .trim()
    .replace(/[_-]+/g, ' ')
    .toLocaleLowerCase(locale)
    .replace(/(^|\s)\S/g, character => character.toLocaleUpperCase(locale))
}

// Some views send a server enum name as is (READYTOSHIP, ONHOLD); it reads the same as its underscored code.
const compactStatusLabels: Record<string, string> = Object.fromEntries(Object.entries(statusLabels)
  .filter(([code]) => code.includes('_')).map(([code, label]) => [code.replaceAll('_', ''), label]))

export function statusLabel(value: string | null | undefined) {
  if (!value?.trim()) return '—'
  const normalized = normalizedStatus(value)
  return statusLabels[normalized] ?? compactStatusLabels[normalized] ?? readableFallback(value)
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
