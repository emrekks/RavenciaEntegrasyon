import { useState } from 'react'
import { ImageLightboxModal } from '../../../catalog/shared'
import { formatIstanbul } from '../../../../shared/format-date'
import { thumbnailUrl } from '../../../../shared/product-thumbnail'

// One period of an uploaded file. A file with more than one period (Trendyol's "3 Gün" and "4 Gün" of a week) lists a
// tariff for each, sharing groupId (the file's first period); periodLabel is the file's name for the period. Both are
// absent in an older server's answer.
export type CommissionTariff = { id: string; platformCode: string; periodStart: string; periodEnd: string; periodText: string; fileName: string; uploadedAt: string; rowCount: number; matchedRowCount: number; isCurrent: boolean
  periodLabel?: string | null; groupId?: string | null }

// The stored end is exclusive (the minute after the file's last one); a period is shown up to its own last minute.
export const lastMinute = (end: string) => new Date(Date.parse(end) - 60_000)
export const shortDay = (value: string | Date, year: boolean) => formatIstanbul(value, year ? { day: 'numeric', month: 'short', year: 'numeric' } : { day: 'numeric', month: 'short' })

// A period's length in whole days (3 for 29 Ekim 08.00 – 1 Kasım 07.59).
export const periodDays = (period: Pick<CommissionTariff, 'periodStart' | 'periodEnd'>) => Math.max(1, Math.round((Date.parse(period.periodEnd) - Date.parse(period.periodStart)) / 86_400_000))
// A period's short name: the file's ("3 Gün"), else its length in days.
export const periodName = (period: CommissionTariff) => period.periodLabel?.trim() || `${periodDays(period)} Gün`

// The tab of a file's periods together: "7 Gün (29 Eki – 5 Kas 2026)".
export function weekTabLabel(periods: CommissionTariff[]) {
  const days = periods.reduce((sum, period) => sum + periodDays(period), 0)
  return `${days} Gün (${shortDay(periods[0].periodStart, false)} – ${shortDay(lastMinute(periods[periods.length - 1].periodEnd), true)})`
}

// The pick for every row: one band ('tier'), or the band leaving the most money ('amount') or the most on its cost
// ('rate'); with a least share of the cost (per cent) or amount (lira) a row whose pick leaves less is left out, with a
// most share or amount one whose pick leaves more.
export type TariffBulk = { kind: '' | 'tier' | 'amount' | 'rate'; tier: number; minRate: number | null; minAmount: number | null; maxRate?: number | null; maxAmount?: number | null }

// A product's photo; a click opens it full size as the products page does (its preview, Escape or a click outside
// closes it), without reaching the row. Without a photo, the name's first letter.
export function ProductImage({ url, title, initial, modelCode, color, size }: { url: string | null | undefined; title: string; initial: string; modelCode?: string | null; color?: string | null; size?: string | null }) {
  const [open, setOpen] = useState(false)
  if (!url) return <i className="commission-product-image" aria-hidden="true">{initial}</i>
  return <>
    <button type="button" className="commission-product-zoom" aria-label={`${title} görselini büyüt`} onClick={event => { event.preventDefault(); event.stopPropagation(); setOpen(true) }}>
      <img src={thumbnailUrl(url)} alt="" loading="lazy" className="commission-product-image" />
    </button>
    {open && <ImageLightboxModal image={{ url, title, modelCode, color, size }} onClose={() => setOpen(false)} />}
  </>
}

// A file's span from its first period's start to its last one's end; the stored end is exclusive (the minute after the
// file's last one), so the label shows the file's own last minute.
export const periodLabel = (periods: CommissionTariff[]) => `${formatIstanbul(periods[0].periodStart, { day: '2-digit', month: 'long', hour: '2-digit', minute: '2-digit' })} – ${formatIstanbul(lastMinute(periods[periods.length - 1].periodEnd), { day: '2-digit', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' })}`

export const tiers = [1, 2, 3, 4]

// Which rows the list shows: by the band their price is in now, and whether their cost is entered.
export type TariffFilter = { tier: '' | '1' | '2' | '3' | '4'; cost: '' | 'with' | 'without'; name: string; barcode: string; model: string; minStock: string; maxStock: string; category: string; brand: string; sort?: TariffSort; dir?: 'asc' | 'desc'
  // Fiyat Aralığı: a band and what its price leaves after commission and cost, in lira and as a share of the price.
  profitTier?: '' | '1' | '2' | '3' | '4'; minProfit?: string; maxProfit?: string; minShare?: string; maxShare?: string
  // The phone's search box: the name, barcode, seller SKU and model code at once (the server's search).
  search?: string }
export const noFilter: TariffFilter = { tier: '', cost: '', name: '', barcode: '', model: '', minStock: '', maxStock: '', category: '', brand: '' }
// A header's order: by name, by the current price, or by what a band leaves.
export type TariffSort = 'name' | 'price' | 'tier1' | 'tier2' | 'tier3' | 'tier4'
