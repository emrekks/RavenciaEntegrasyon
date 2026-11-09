import type { QueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import type { CommissionBand, CommissionChoice, CommissionTariff, CommissionTariffRow } from './CommissionTariffsPanel'

export type RowPage = { items: CommissionTariffRow[]; totalCount: number; page: number; pageSize: number }

// The short form of a page of tariff rows the server sends for rows?shape=compact (CommissionTariffCompactRows.cs): the
// values in a fixed order, a model's repeated name and picture as indexes into strings, and each priced band's product
// cost, cargo, service fee, VAT rate and rate left out where the row or its band already carries them.
type CompactBand = [number, number | null, number | null, number]
type CompactCosts = [number, number] | [number, number, number, number, number, number] | null
type CompactChoice = [number, number, number, number | null, CompactCosts] | [number, number, number, number | null, CompactCosts, number]
type CompactRow = [
  id: string, barcode: string, sellerSku: string | null, modelCode: string | null, productName: number | null, size: string | null,
  basisPrice: number | null, currentSalePrice: number | null, currentRate: number | null, currentTier: number | null, currentBandRate: number | null,
  bands: CompactBand[], nextLowerBand: CompactBand | null, matchesPanel: boolean, costPrice: number | null, choices: CompactChoice[] | null,
  recommendedTier: number | null, imageUrl: number | null, color: string | null, stock: number | null, shared: [number, number, number, number] | null
]
export type CompactRowPage = { strings: string[]; rows: CompactRow[]; totalCount: number; page: number; pageSize: number }

const band = ([tier, min, max, rate]: CompactBand): CommissionBand => ({ tier, min, max, rate })

// The page as the long form gives it, field for field.
export function expandRowPage(raw: CompactRowPage): RowPage {
  const text = (index: number | null) => index === null ? null : raw.strings[index]
  return {
    items: raw.rows.map(([id, barcode, sellerSku, modelCode, productName, size, basisPrice, currentSalePrice, currentRate, currentTier, currentBandRate, bands,
      nextLowerBand, matchesPanel, costPrice, choices, recommendedTier, imageUrl, color, stock, shared]) => {
      const rowBands = bands.map(band)
      const choice = ([tier, price, commission, remaining, costs, rate]: CompactChoice): CommissionChoice => ({
        tier, price, rate: rate ?? rowBands.find(item => item.tier === tier)!.rate, commission, remaining,
        costs: costs === null ? null
          : costs.length === 2 ? { cost: shared![0], cargo: shared![1], serviceFee: shared![2], stoppage: costs[0], netVat: costs[1], vatRate: shared![3] }
            : { cost: costs[0], cargo: costs[1], serviceFee: costs[2], stoppage: costs[3], netVat: costs[4], vatRate: costs[5] }
      })
      return {
        id, barcode, sellerSku, modelCode, productName: text(productName), size, basisPrice, currentSalePrice, currentRate, currentTier, currentBandRate,
        bands: rowBands, nextLowerBand: nextLowerBand === null ? null : band(nextLowerBand), matchesPanel, costPrice,
        choices: choices === null ? null : choices.map(choice), recommendedTier, imageUrl: text(imageUrl), color, stock
      }
    }),
    totalCount: raw.totalCount, page: raw.page, pageSize: raw.pageSize
  }
}

// A page of a tariff's rows, asked for in the short form and given back in the long one.
export async function loadRowPage(connectionId: string, tariffId: string, query: Record<string, string>): Promise<RowPage> {
  const params = new URLSearchParams({ ...query, shape: 'compact' })
  return expandRowPage(await hubApi<CompactRowPage>(`/connections/${connectionId}/commission-tariffs/${tariffId}/rows?${params}`))
}

// The tariffs a store's tab lists, and the tab's first page of rows; the panel and the prefetch below share them.
export const tariffsQuery = (connectionId: string) => ({
  queryKey: ['commission-tariffs', connectionId],
  queryFn: () => hubApi<CommissionTariff[]>(`/connections/${connectionId}/commission-tariffs`)
})

// The store and tariff the tab last opened on by itself (no period picked), kept in this browser only.
const lastTariffKey = 'rv-commission-tariff-last'
type LastTariff = { connectionId: string; tariffId: string }

export function rememberTariff(last: LastTariff) {
  try { localStorage.setItem(lastTariffKey, JSON.stringify(last)) } catch { /* storage off: nothing is prefetched next time */ }
}

function lastTariff(): LastTariff | null {
  try {
    const value = JSON.parse(localStorage.getItem(lastTariffKey) ?? 'null') as Partial<LastTariff> | null
    return typeof value?.connectionId === 'string' && typeof value.tariffId === 'string' ? { connectionId: value.connectionId, tariffId: value.tariffId } : null
  } catch {
    return null
  }
}

// Opening Promosyon, the tariff list and the first page of rows the tab last opened on are asked for at once, beside
// the stores, instead of one after the other (stores, then tariffs, then rows). The panel's own queries take them over
// when they are the ones it opens on; otherwise they are only not used.
export function prefetchLastTariff(client: QueryClient, store: string | null, rowsQuery: (connectionId: string, tariffId: string) => { queryKey: unknown[]; queryFn: () => Promise<RowPage> }) {
  const last = lastTariff()
  if (!last || (store && store !== last.connectionId)) return
  void client.prefetchQuery(tariffsQuery(last.connectionId))
  void client.prefetchQuery(rowsQuery(last.connectionId, last.tariffId))
}
