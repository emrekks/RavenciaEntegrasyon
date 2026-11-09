import type { CommissionBand, CommissionTariffRow } from './CommissionTariffsPanel'
import type { CompactRowPage, RowPage } from './commission-tariff-rows'

// The short form the server writes (CommissionTariffCompactRows.cs), for tests that answer rows?shape=compact.
export function compactRowPage(page: Partial<RowPage> & { items: CommissionTariffRow[] }): CompactRowPage {
  const strings: string[] = []
  const text = (value: string | null | undefined) => {
    if (value == null) return null
    const index = strings.indexOf(value)
    return index >= 0 ? index : strings.push(value) - 1
  }
  const band = (item: CommissionBand) => [item.tier, item.min, item.max, item.rate] as [number, number | null, number | null, number]
  const rows = page.items.map(row => {
    const priced = (row.choices ?? []).flatMap(choice => choice.costs ? [choice.costs] : [])
    const first = priced[0]
    const shared = first && priced.every(costs => costs.cost === first.cost && costs.cargo === first.cargo && costs.serviceFee === first.serviceFee && costs.vatRate === first.vatRate)
      ? [first.cost, first.cargo, first.serviceFee, first.vatRate] as [number, number, number, number] : null
    const choices = row.choices == null ? null : row.choices.map(choice => {
      const costs = !choice.costs ? null : shared ? [choice.costs.stoppage, choice.costs.netVat] as [number, number]
        : [choice.costs.cost, choice.costs.cargo, choice.costs.serviceFee, choice.costs.stoppage, choice.costs.netVat, choice.costs.vatRate] as [number, number, number, number, number, number]
      const bandRate = row.bands.find(item => item.tier === choice.tier)?.rate
      return bandRate === choice.rate ? [choice.tier, choice.price, choice.commission, choice.remaining, costs] as const : [choice.tier, choice.price, choice.commission, choice.remaining, costs, choice.rate] as const
    })
    return [row.id, row.barcode, row.sellerSku, row.modelCode, text(row.productName), row.size, row.basisPrice, row.currentSalePrice, row.currentRate, row.currentTier,
      row.currentBandRate, row.bands.map(band), row.nextLowerBand ? band(row.nextLowerBand) : null, row.matchesPanel, row.costPrice ?? null, choices,
      row.recommendedTier ?? null, text(row.imageUrl), row.color ?? null, row.stock ?? null, shared]
  })
  return { strings, rows: rows as unknown as CompactRowPage['rows'], totalCount: page.totalCount ?? page.items.length, page: page.page ?? 0, pageSize: page.pageSize ?? 50 }
}
