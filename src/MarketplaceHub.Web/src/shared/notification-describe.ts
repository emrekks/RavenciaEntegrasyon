import type { AppNotification } from './notifications'

const operations: Array<[RegExp, string]> = [
  [/iade kabul/u, 'İade kabul kararı'],
  [/iade (?:ret\b|reddi|reddedil)/u, 'İade ret kararı'],
  [/iade.*(?:eşitle|senkron)|(?:eşitle|senkron).*iade/u, 'İade senkronizasyonu'],
  [/(?:kategori|marka|özellik|değer|renk).*(?:eşitle|senkron)/u, 'Referans verisi eşitleme'],
  [/dış yazma|yazma anahtar|yazma grubu/u, 'Dış yazma ayarı'],
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
  ['/mappings', 'Eşleştirmeler'], ['/jobs', 'İşlem Takibi'], ['/integrations', 'Entegrasyonlar'], ['/shipments', 'Gönderiler'], ['/settings', 'Ayarlar']
]

const platforms: Array<[RegExp, string]> = [[/trendyol/u, 'Trendyol'], [/hepsiburada/u, 'Hepsiburada'], [/shopify/u, 'Shopify'], [/n11/u, 'n11'], [/pazarama/u, 'Pazarama'], [/pttavm/u, 'PttAVM']]

export type NotificationFact = { label: string; value: string; tone?: 'success' | 'error' }

// A short, formal summary of what a notification is about. The sentence itself is already shown above
// it, so it is only repeated here when it carries extra text after the first sentence.
export function describeNotification(notification: AppNotification): NotificationFact[] {
  const text = notification.message.toLocaleLowerCase('tr-TR')
  const facts: NotificationFact[] = []
  const operation = operations.find(([pattern]) => pattern.test(text))?.[1]
  if (operation) facts.push({ label: 'İşlem', value: operation })
  const path = notification.href?.split('?')[0] ?? ''
  const area = areas.find(([prefix]) => path.startsWith(prefix))?.[1]
  const named = platforms.filter(([pattern]) => pattern.test(text)).map(([, name]) => name)
  const source = [area, named.length > 0 ? named.join(', ') : null].filter(Boolean).join(' · ')
  if (source) facts.push({ label: 'Kaynak', value: source })
  const queued = /kuyruğa alındı|kuyrukta/u.test(text)
  facts.push({
    label: 'Sonuç',
    value: notification.kind === 'error' ? 'Başarısız · İnceleme gerekiyor' : queued ? 'Kuyruğa alındı · İşlem Takibi’nden izlenebilir' : notification.kind === 'success' ? 'Tamamlandı' : 'Bilgi',
    tone: notification.kind === 'error' ? 'error' : notification.kind === 'success' ? 'success' : undefined
  })
  const extra = notification.message.split(' · ').map(part => part.trim()).filter(Boolean).slice(1).join(' · ')
  if (extra) facts.push({ label: 'Açıklama', value: extra })
  return facts
}
