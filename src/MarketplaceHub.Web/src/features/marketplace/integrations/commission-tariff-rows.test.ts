import { QueryClient } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { CommissionTariffRow } from './CommissionTariffsPanel'
import { compactRowPage } from './commission-tariff-rows.test-util'
import { expandRowPage, prefetchLastTariff, rememberTariff } from './commission-tariff-rows'

// A live row (RYP004-07, 9 Nov 2026) as the long form gave it, and one whose bands' costs differ and whose band rate
// is not its choice's rate.
const costs = (stoppage: number, netVat: number) => ({ cost: 154, cargo: 98.34, serviceFee: 13.19, stoppage, netVat, vatRate: 10 })
const live: CommissionTariffRow = {
  id: '01a18ef4-df2c-737c-9979-d5429aedbe4e', barcode: 'RYP004-07', sellerSku: 'RYP004-07', modelCode: 'RYP004', productName: 'Baskı Desenli Simli Likralı Yırtmaçlı Kadın Anne Penye Bluz', size: 'M',
  basisPrice: 452.43, currentSalePrice: 457, currentRate: 17.5, currentTier: 4, currentBandRate: 17.5,
  bands: [{ tier: 1, min: 563.84, max: null, rate: 21.5 }, { tier: 2, min: 510.42, max: 563.83, rate: 20.8 }, { tier: 3, min: 457.01, max: 510.41, rate: 19.3 }, { tier: 4, min: null, max: 457, rate: 17.5 }],
  nextLowerBand: null, matchesPanel: true, costPrice: 154,
  choices: [
    { tier: 1, price: 563.84, rate: 21.5, commission: 121.23, remaining: 173.49, costs: costs(5.13, -1.54) },
    { tier: 2, price: 563.83, rate: 20.8, commission: 117.28, remaining: 176.77, costs: costs(5.13, -0.88) },
    { tier: 3, price: 510.41, rate: 19.3, commission: 98.51, remaining: 144.34, costs: costs(4.64, -2.61) },
    { tier: 4, price: 457, rate: 17.5, commission: 79.98, remaining: 111.71, costs: costs(4.15, -4.37) }
  ],
  recommendedTier: 2, imageUrl: 'https://cdn.dsmcdn.com/ty1972/prod/QC_PREP/20261007/17/d2a507c0-967f-3612-a714-3a03b7099f84/1_org_zoom.jpg', color: 'Lacivert', stock: 2
}
const odd: CommissionTariffRow = {
  ...live, id: 'odd', barcode: 'RYP004-08', size: 'L', stock: null, color: null, imageUrl: null, costPrice: null, recommendedTier: null, currentTier: null, currentBandRate: null,
  nextLowerBand: { tier: 4, min: null, max: 456.99, rate: 17.5 },
  choices: [
    { tier: 1, price: 563.84, rate: 21, commission: 118.41, remaining: null, costs: null },
    { tier: 2, price: 563.83, rate: 20.8, commission: 117.28, remaining: 150, costs: { ...costs(5.13, -0.88), cargo: 60 } },
    { tier: 3, price: 510.41, rate: 19.3, commission: 98.51, remaining: 144.34, costs: costs(4.64, -2.61) }
  ]
}

describe('commission tariff rows in the short form', () => {
  afterEach(() => localStorage.clear())

  it('puts every field of the long form back as it was', () => {
    const page = { items: [live, { ...live, id: 'xl', size: 'XL' }, odd, { ...odd, id: 'none', choices: null }], totalCount: 38, page: 0, pageSize: 50 }
    const compact = compactRowPage(page)
    // The model's name and picture are written once.
    expect(compact.strings).toEqual([live.productName, live.imageUrl])
    expect(expandRowPage(compact)).toEqual(page)
    expect(JSON.stringify(compact).length).toBeLessThan(JSON.stringify(page).length / 2)
  })

  it('asks for the tariff the tab last opened on beside the stores, only for that store', () => {
    const client = new QueryClient()
    const prefetch = vi.spyOn(client, 'prefetchQuery').mockResolvedValue(undefined)
    const rowsQuery = (connectionId: string, tariffId: string) => ({ queryKey: ['commission-tariff-rows', connectionId, tariffId], queryFn: async () => compactRowPage({ items: [] }) as never })
    prefetchLastTariff(client, null, rowsQuery)
    expect(prefetch).not.toHaveBeenCalled()
    rememberTariff({ connectionId: 'store-1', tariffId: 't1' })
    prefetchLastTariff(client, 'store-2', rowsQuery)
    expect(prefetch).not.toHaveBeenCalled()
    prefetchLastTariff(client, null, rowsQuery)
    expect(prefetch.mock.calls.map(([options]) => options.queryKey)).toEqual([['commission-tariffs', 'store-1'], ['commission-tariff-rows', 'store-1', 't1']])
  })
})
