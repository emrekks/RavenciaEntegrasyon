import type { ApiAnswer } from '../../../shared/api/api-answer'
import { queryKeys } from '../../../shared/api/query-keys'

// Owner decision of 18 Nov 2026: "Panel Fiyatlarını İlk Pazaryeri Fiyatından Doldur". The read-only preview and the
// sentences the confirmation shows; the fill writes only empty panel prices and sends nothing to a marketplace.
export type PanelPriceFillPreview = ApiAnswer<'getChannelOffersPanelPriceFillPreview'>
export const panelPriceFillPreviewPath = '/channel-offers/panel-price-fill-preview'
export const panelPriceFillPath = '/channel-offers/panel-price-fill'
export const panelPriceFillPreviewKey = queryKeys.products('panel-price-fill-preview')

const count = (value: number) => value.toLocaleString('tr-TR')
const money = (value: number) => value.toLocaleString('tr-TR', { minimumFractionDigits: 0, maximumFractionDigits: 2 })

export function panelPriceFillSummary(preview: PanelPriceFillPreview): string | null {
  if (preview.fillable <= 0) return null
  const sources = Object.entries(preview.fillableBySource).map(([platform, value]) => `${platform} ${count(value)}`).join(', ')
  return `Panel fiyatı boş ${count(preview.withoutPanelPrice)} beden: ${count(preview.fillable)} tanesi ilk pazaryeri fiyatından doldurulabilir (${sources}).`
}

export function panelPriceFillConfirmLines(preview: PanelPriceFillPreview): string[] {
  const lines = [`${count(preview.fillable)} bedenin panel fiyatı, ilk çekildiği pazaryerinin fiyatıyla doldurulacak; dolu panel fiyatlarına dokunulmaz.`]
  if (preview.zeroPrice > 0) lines.push(`İlk pazaryeri fiyatı 0 TL olan ${count(preview.zeroPrice)} beden boş kalacak.`)
  if (preview.withoutOffer > 0) lines.push(`Teklifi olmayan ${count(preview.withoutOffer)} beden boş kalacak.`)
  for (const change of preview.offerChanges)
    lines.push(`${change.platformCode}: Özel Fiyat'ı kapalı ${count(change.offers)} teklifin kayıtlı fiyatı panel fiyatına çekilecek (toplam fark ${money(change.totalDifference)} TL, ortanca ${money(change.medianDifference)} TL, en büyük ${money(change.largestDifference)} TL).`)
  lines.push('Şimdi pazaryerine hiçbir şey gönderilmeyecek; fiyat, bedenin bir sonraki stok ya da fiyat gönderiminde gider.')
  return lines
}
