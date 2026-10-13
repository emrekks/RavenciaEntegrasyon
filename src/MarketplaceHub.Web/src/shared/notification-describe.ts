import type { AppNotification } from './notifications'

const operations: Array<[RegExp, string]> = [
  [/iade kabul/u, 'İade kabulü (pazar yerine gönderim)'],
  [/iade (?:reddi|reddedil)/u, 'İade reddi (pazar yerine gönderim)'],
  [/iade.*(?:eşitle|senkron)|(?:eşitle|senkron).*iade/u, 'İade senkronizasyonu (pazar yerinden okuma)'],
  [/(?:kategori|marka|özellik|değer|renk).*(?:eşitle|senkron)/u, 'Referans verisi eşitleme (pazar yerinden okuma)'],
  [/dış yazma|yazma anahtar|yazma grubu/u, 'Entegrasyon yazma izni'],
  [/etiket/u, 'Kargo etiketi'],
  [/fatura/u, 'Fatura işlemi'],
  [/paket|kargo/u, 'Paket ve kargo işlemi'],
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

export function describeNotification(notification: AppNotification): NotificationFact[] {
  const text = notification.message.toLocaleLowerCase('tr-TR')
  const facts: NotificationFact[] = []
  const operation = operations.find(([pattern]) => pattern.test(text))?.[1]
  if (operation) facts.push({ label: 'İşlem', value: operation })
  const path = notification.href?.split('?')[0] ?? ''
  const area = areas.find(([prefix]) => path.startsWith(prefix))?.[1]
  if (area) facts.push({ label: 'Alan', value: area })
  const named = platforms.filter(([pattern]) => pattern.test(text)).map(([, name]) => name)
  if (named.length > 0) facts.push({ label: 'Pazar Yeri', value: named.join(', ') })
  const queued = /kuyruğa alındı|kuyrukta/u.test(text)
  facts.push({
    label: 'Sonuç',
    value: notification.kind === 'error' ? 'Başarısız, incelenmesi gerekiyor' : queued ? 'Arka planda çalışıyor; sonucu İşlem Takibi sayfasından izlenebilir' : notification.kind === 'success' ? 'Başarıyla tamamlandı' : 'Bilgi',
    tone: notification.kind === 'error' ? 'error' : notification.kind === 'success' ? 'success' : undefined
  })
  return facts
}
