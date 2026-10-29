import { notificationPlatformName, notificationSubjectLabels, type AppNotification, type NotificationSubject } from './notifications'
import { formatDateTime } from './format-date'

export type NotificationFact = { label: string; value: string; tone?: 'success' | 'error'; href?: string }

// Titles of notifications whose producer did not name one (and of every notification saved before titles existed):
// the operation and its outcome, read from the sentence. The first match wins, so the specific ones come first.
const titleRules: Array<[RegExp, string, string, string]> = [
  // pattern, title on success, title on failure, title otherwise
  [/yayın uyarısı/u, 'Yayın Uyarısı', 'Yayın Uyarısı', 'Yayın Uyarısı'],
  [/yayın durum/u, 'Yayın Durumu Güncellendi', 'Yayın Durumu Sorgulanamadı', 'Yayın Durumu Sorgulanıyor'],
  [/bağlantı testi/u, 'Bağlantı Testi Başarılı', 'Bağlantı Testi Başarısız', 'Bağlantı Testi'],
  [/iade kabul/u, 'İade Kabul Kararı İletildi', 'İade Kabul Kararı İletilemedi', 'İade Kabul Kararı'],
  [/iade (?:ret\b|reddi|reddedil)/u, 'İade Ret Kararı İletildi', 'İade Ret Kararı İletilemedi', 'İade Ret Kararı'],
  [/iade.*(?:eşitle|senkron)|(?:eşitle|senkron).*iade/u, 'İadeler Eşitlendi', 'İadeler Eşitlenemedi', 'İade Eşitlemesi'],
  [/dış yazma|yazma anahtar|yazma grubu/u, 'Pazaryerine Yazma Ayarı Değişti', 'Pazaryerine Yazma Kapalı', 'Pazaryerine Yazma Ayarı'],
  [/sıfırlandı/u, 'Bağlantı Verileri Sıfırlandı', 'Bağlantı Verileri Sıfırlanamadı', 'Bağlantı Verileri'],
  [/etiket/u, 'Kargo Etiketi Hazır', 'Kargo Etiketi Alınamadı', 'Kargo Etiketi'],
  [/e-?arşiv|e-?fatura|fatura/u, 'Fatura İşlemi Tamamlandı', 'Fatura İşlemi Başarısız', 'Fatura İşlemi'],
  [/paket|kargo/u, 'Paket Güncellendi', 'Paket İşlemi Başarısız', 'Paket İşlemi'],
  [/fiyat.*stok|stok.*fiyat/u, 'Fiyat ve Stok Güncellendi', 'Fiyat ve Stok Güncellenemedi', 'Fiyat ve Stok'],
  [/stok|stoğa/u, 'Stok Güncellendi', 'Stok Güncellenemedi', 'Stok Güncelleme'],
  [/fiyat/u, 'Fiyat Güncellendi', 'Fiyat Güncellenemedi', 'Fiyat Güncelleme'],
  [/yayın|yayına/u, 'Ürün Yayına Gönderildi', 'Ürün Yayınlanamadı', 'Ürün Yayını'],
  [/ürün oluşturuldu/u, 'Ürün Oluşturuldu', 'Ürün Oluşturulamadı', 'Ürün Oluşturma'],
  [/oturum|parola|doğrulama kodu/u, 'Hesap Güvenliği Güncellendi', 'Hesap Güvenliği İşlemi Başarısız', 'Hesap Güvenliği'],
  [/silindi|temizlik/u, 'Kayıtlar Silindi', 'Kayıtlar Silinemedi', 'Kayıt Silme'],
  [/görsel/u, 'Görseller Güncellendi', 'Görsel İşlemi Başarısız', 'Görsel İşlemi'],
  [/sipariş/u, 'Sipariş Güncellendi', 'Sipariş İşlemi Başarısız', 'Sipariş İşlemi'],
  [/eşleş|kategori|marka|özellik/u, 'Eşleştirme Güncellendi', 'Eşleştirme Başarısız', 'Eşleştirme'],
  [/yüklenemedi/u, 'Veriler Yüklenemedi', 'Veriler Yüklenemedi', 'Veriler Yüklenemedi'],
  [/eşitle|senkron/u, 'Eşitleme Tamamlandı', 'Eşitleme Başarısız', 'Eşitleme']
]

const operationRules: Array<[RegExp, string]> = [
  [/yayın durum/u, 'Yayın durumu sorgulama'],
  [/bağlantı testi/u, 'Bağlantı testi'],
  [/iade kabul/u, 'İade kabul kararı'],
  [/iade (?:ret\b|reddi|reddedil)/u, 'İade ret kararı'],
  [/iade.*(?:eşitle|senkron)|(?:eşitle|senkron).*iade/u, 'İade senkronizasyonu'],
  [/(?:kategori|marka|özellik|değer|renk).*(?:eşitle|senkron)/u, 'Referans verisi eşitleme'],
  [/dış yazma|yazma anahtar|yazma grubu/u, 'Pazaryerine yazma ayarı'],
  [/etiket/u, 'Kargo etiketi'],
  [/fatura/u, 'Fatura işlemi'],
  [/paket|kargo/u, 'Paket işlemi'],
  [/stok|stoğa/u, 'Stok güncelleme'],
  [/yayın/u, 'Ürün yayını'],
  [/oturum|parola|doğrulama kodu/u, 'Hesap güvenliği'],
  [/ürün|katalog|varyant/u, 'Katalog işlemi'],
  [/sipariş/u, 'Sipariş işlemi'],
  [/eşleş|kategori|marka/u, 'Eşleştirme işlemi']
]

const areas: Array<[string, string]> = [
  ['/returns', 'İade Yönetimi'], ['/orders', 'Siparişler'], ['/invoices', 'Faturalar'], ['/products', 'Ürün Kataloğu'],
  ['/mappings', 'Eşleştirmeler'], ['/jobs', 'İşlem Takibi'], ['/integrations', 'Entegrasyonlar'], ['/shipments', 'Gönderiler'], ['/settings', 'Ayarlar'],
  ['/imports', 'İçe Aktarım'], ['/questions', 'Müşteri Soruları']
]

const platformPatterns: Array<[RegExp, string]> = [[/trendyol/u, 'TRENDYOL'], [/hepsiburada/u, 'HEPSIBURADA'], [/shopify/u, 'SHOPIFY'], [/\bn11\b/u, 'N11'], [/pazarama/u, 'PAZARAMA'], [/ptt ?avm/u, 'PTTAVM']]

// The marketplace a sentence names, when it names exactly one ("Pazarama: …"); a list of several names none.
export function platformNamedIn(message: string): string | null {
  const text = message.toLocaleLowerCase('tr-TR')
  const named = platformPatterns.filter(([pattern]) => pattern.test(text)).map(([, code]) => code)
  return named.length === 1 ? named[0] : null
}

// Notifications saved by the old toast bridge start with the toast title ("Durum Sorgulanamadı: …"): a few Title Case words.
function splitLegacyTitle(message: string): { title: string; rest: string } | null {
  const match = /^([^:·]{3,48}):\s+(.+)$/su.exec(message)
  if (!match) return null
  const words = match[1].trim().split(/\s+/u)
  if (words.length > 5 || !words.every(word => /^[A-ZÇĞİÖŞÜ0-9]/u.test(word))) return null
  if (platformNamedIn(match[1]) && words.length === 1) return null
  return { title: match[1].trim(), rest: match[2].trim() }
}

export function deriveNotificationTitle(message: string, kind: AppNotification['kind']) {
  const text = message.toLocaleLowerCase('tr-TR')
  const rule = titleRules.find(([pattern]) => pattern.test(text))
  if (rule) return kind === 'success' ? rule[1] : kind === 'error' ? rule[2] : rule[3]
  return kind === 'error' ? 'İşlem Başarısız' : kind === 'success' ? 'İşlem Tamamlandı' : 'Bilgi'
}

export type NotificationPresentation = {
  title: string
  summary: string
  platformCode: string | null
  platformName: string | null
  storeName: string | null
  area: string | null
  subject: NotificationSubject | null
  count: number
  items: NotificationSubject[]
}

export function presentNotification(notification: AppNotification): NotificationPresentation {
  const legacy = notification.title ? null : splitLegacyTitle(notification.message)
  const sentence = legacy?.rest ?? notification.message
  const summaryText = sentence.split(' · ').map(part => part.trim()).filter(Boolean)[0] ?? sentence
  const count = notification.count ?? 1
  const title = notification.title ?? legacy?.title ?? deriveNotificationTitle(sentence, notification.kind)
  const platformCode = notification.platformCode ?? platformNamedIn(`${legacy?.title ?? ''} ${sentence}`)
  const platformName = notificationPlatformName(platformCode)
  const storeName = notification.storeName && notification.storeName.toLocaleLowerCase('tr-TR') !== platformName?.toLocaleLowerCase('tr-TR') ? notification.storeName : null
  const path = notification.href?.split('?')[0] ?? ''
  const area = areas.find(([prefix]) => path.startsWith(prefix))?.[1] ?? null
  const items = notification.items ?? []
  // "{count} ürün için …" counts the records a burst touched; repeats of one record keep the sentence and the count badge.
  const summary = items.length > 1 && notification.groupMessage ? notification.groupMessage.replaceAll('{count}', String(items.length)) : summaryText
  return { title, summary, platformCode, platformName, storeName, area, subject: notification.subject ?? null, count, items }
}

// The structured Ayrıntı rows: where, what, which record, the outcome, the code and when. The sentence itself is
// already shown above them, so only the text after its first part is repeated here.
export function describeNotification(notification: AppNotification): NotificationFact[] {
  const presentation = presentNotification(notification)
  const text = notification.message.toLocaleLowerCase('tr-TR')
  const facts: NotificationFact[] = []
  if (presentation.platformName) facts.push({ label: 'Platform', value: presentation.platformName })
  if (presentation.storeName) facts.push({ label: 'Mağaza', value: presentation.storeName })
  else if (!presentation.platformName && presentation.area) facts.push({ label: 'Bölüm', value: presentation.area })
  const operation = notification.operation ?? operationRules.find(([pattern]) => pattern.test(text))?.[1]
  if (operation) facts.push({ label: 'İşlem', value: operation })
  if (presentation.subject && presentation.items.length <= 1) facts.push({ label: notificationSubjectLabels[presentation.subject.kind], value: presentation.subject.label, href: presentation.subject.href })
  const queued = /kuyruğa alındı|kuyrukta/u.test(text)
  facts.push({
    label: 'Sonuç',
    value: notification.kind === 'error' ? 'Başarısız · İnceleme gerekiyor' : queued ? 'Kuyruğa alındı · İşlem Takibi’nden izlenebilir' : notification.kind === 'success' ? 'Tamamlandı' : 'Bilgi',
    tone: notification.kind === 'error' ? 'error' : notification.kind === 'success' ? 'success' : undefined
  })
  if (notification.code) facts.push({ label: 'Kod', value: notification.code })
  const sentence = splitLegacyTitle(notification.message)?.rest ?? notification.message
  const extra = sentence.split(' · ').map(part => part.trim()).filter(Boolean).slice(1).join(' · ')
  if (extra) facts.push({ label: 'Açıklama', value: extra })
  if (presentation.count > 1) {
    facts.push({ label: 'Tekrar', value: `${presentation.count} kez` })
    // The full sentence of the latest one, when the summary shows the collapsed count instead.
    if (notification.groupMessage && presentation.items.length > 1) facts.push({ label: 'Son Bildirim', value: sentence })
  }
  if (notification.jobId) facts.push({ label: 'İş No', value: notification.jobId })
  facts.push({
    label: 'Zaman',
    value: presentation.count > 1 && notification.firstAt && notification.firstAt !== notification.createdAt
      ? `${formatDateTime(notification.firstAt)} – ${formatDateTime(notification.createdAt)}`
      : formatDateTime(notification.createdAt)
  })
  return facts
}
