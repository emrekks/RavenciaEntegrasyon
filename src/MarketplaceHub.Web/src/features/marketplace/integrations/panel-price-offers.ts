import type { ApiAnswer } from '../../../shared/api/api-answer'
import { queryKeys } from '../../../shared/api/query-keys'

// Owner rule of 18 Nov 2026: a variant linked to a listing without an offer there goes at the panel price; Özel Fiyat
// keeps its own. GET /connections/{id}/panel-price-offers counts it per connection (read only).
export type PanelPriceOfferPreview = ApiAnswer<'getConnectionsByIdPanelPriceOffers'>

export const panelPriceOffersPath = (connectionId: string) => `/connections/${connectionId}/panel-price-offers`
export const panelPriceOffersQueryKey = (connectionId: string) => queryKeys.panelPriceOffers(connectionId)

const count = (value: number) => value.toLocaleString('tr-TR')

// The sentences the switch's confirmation adds: what opens now (nothing is sent) and what stays out, by the server's
// counts. Empty when the connection has no linked size without an offer.
export function panelPriceOfferNotice(preview: PanelPriceOfferPreview | null | undefined): string[] {
  if (!preview) return ['Panel fiyatıyla açılacak teklif sayısı okunamadı.']
  const lines: string[] = []
  // Every stock send carries the price in force (owner, 18 Nov 2026): the first send of an opened offer carries its panel price.
  if (preview.opensOffers && preview.openable > 0) lines.push(`${count(preview.openable)} beden için panel fiyatıyla teklif açılacak; şimdi pazaryerine bir şey gönderilmeyecek; bedenin bir sonraki stok gönderiminde stokla birlikte geçerli fiyat da gider.`)
  if (!preview.opensOffers && preview.linkedWithoutOffer > 0) lines.push(`${count(preview.linkedWithoutOffer)} bağlı bedenin bu bağlantıda teklifi yok; bu bağlantıda panel fiyatıyla teklif açılmaz.`)
  if (preview.opensOffers && preview.missingPanelPrice > 0) lines.push(`Panel fiyatı olmayan ${count(preview.missingPanelPrice)} beden gönderime alınmadı.`)
  if (preview.priceChangesOnFirstStockSend > 0) lines.push(`Kapalıyken fiyatı değişmiş ${count(preview.priceChangesOnFirstStockSend)} teklifin yeni fiyatı, bedenin ilk stok hareketinde gidecek.`)
  if (preview.opensOffers && preview.missingVat > 0) lines.push(`KDV oranı olmayan ${count(preview.missingVat)} beden gönderime alınmadı.`)
  return lines
}

// The short line under Otomatik Dış Yazmalar: the counts and whether "Eksik Teklifleri Aç" has anything to open.
export function panelPriceOfferSummary(preview: PanelPriceOfferPreview): { text: string; canOpen: boolean } | null {
  const parts: string[] = []
  if (preview.linkedWithoutOffer > 0) parts.push(`Teklifsiz bağlı ${count(preview.linkedWithoutOffer)} beden`)
  if (preview.opensOffers && preview.openable > 0) parts.push(`${count(preview.openable)} panel fiyatıyla açılabilir`)
  if (preview.missingPanelPrice > 0) parts.push(`${count(preview.missingPanelPrice)} panel fiyatı yok`)
  if (preview.missingVat > 0) parts.push(`${count(preview.missingVat)} KDV yok`)
  if (preview.followingAtOtherPrice > 0) parts.push(`panel fiyatından farklı ${count(preview.followingAtOtherPrice)} teklif`)
  if (!parts.length) return null
  return { text: parts.join(' · '), canOpen: preview.opensOffers && preview.openable > 0 }
}
