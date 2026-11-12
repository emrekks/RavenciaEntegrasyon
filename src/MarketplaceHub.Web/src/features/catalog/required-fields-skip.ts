// Toplu Satışa Aç and Toplu Değer Ata › Satış Durumu (owner, 13 Nov 2026: "Eksik olanı atlasın, bildirsin
// bildirimlerde"): the server opens the complete cards and returns each closed card it skipped for a missing required
// field. The notification names them, lists what each misses, and leads to the closed cards in the product list.
import type { NotificationDetail, NotificationTarget } from '../../shared/notifications'

export type RequiredFieldsSkipped = { productId: string; title: string; modelCode?: string | null; missingFields: string[] }

const namedInMessage = 3
const detailLimit = 40

function cardName(card: RequiredFieldsSkipped) {
  const title = card.title.trim()
  const model = card.modelCode?.trim()
  return model && !title.includes(model) ? `${title} (${model})` : title || model || 'Adsız ürün'
}

// The skipped cards stay closed, so the list opens on Satışa Kapalı; when they are all of one model, on that model.
export function requiredFieldsSkipHref(skipped: readonly RequiredFieldsSkipped[]): string {
  const params = new URLSearchParams({ status: 'ARCHIVED' })
  const models = new Set(skipped.map(card => card.modelCode?.trim() ?? ''))
  const [only] = models
  if (models.size === 1 && only) params.set('q', only)
  return `/products?${params.toString()}`
}

export function requiredFieldsSkipNotice(skipped: readonly RequiredFieldsSkipped[]): { message: string; details: NotificationDetail[]; target: NotificationTarget } | null {
  if (!skipped.length) return null
  const count = skipped.length.toLocaleString('tr-TR')
  const named = skipped.slice(0, namedInMessage).map(card => `${cardName(card)}: ${card.missingFields.join(', ')}`).join('; ')
  const more = skipped.length > namedInMessage ? ` ve ${(skipped.length - namedInMessage).toLocaleString('tr-TR')} ürün daha` : ''
  return {
    message: `${count} ürün eksik alan nedeniyle satışa açılmadı: ${named}${more}. Ürün düzenleme ekranından tamamlayıp tekrar deneyin.`,
    details: skipped.slice(0, detailLimit).map(card => ({ label: cardName(card), value: `Eksik: ${card.missingFields.join(', ')}`, tone: 'error' as const })),
    target: { href: requiredFieldsSkipHref(skipped), label: 'Eksik Ürünleri Göster' }
  }
}
