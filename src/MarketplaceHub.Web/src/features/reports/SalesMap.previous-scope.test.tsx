import { act } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import type { ReportGroup, ReportSales } from './report-format'
import { SalesMap } from './SalesMap'

afterEach(cleanup)

const group = (key: string, label: string, netRevenue: number, known = label !== 'Bilinmiyor'): ReportGroup => ({
  key, label, known, isTest: false, orders: 1, soldQuantity: 1, returnedQuantity: 0, returnClaims: 0, returnRate: 0, ordersWithAmount: 1,
  grossSales: netRevenue, netRevenue, estimatedCommission: 0, commissionCoveredRevenue: 0, children: [], split: []
})

const strip = (host: HTMLElement) => [...host.querySelectorAll<HTMLElement>('.reports-map-strip > div')].find(cell => cell.textContent?.includes('Önceki Döneme Göre'))!

// Scan C-8: the previous period kept the unknown province (and Turkey in the world view) that this period leaves out,
// so the same sales read as a fall ("−%95"). Both periods now pass the same filter.
describe('the sales map against the previous period', () => {
  it('compares the provinces with the same known provinces', async () => {
    const data = {
      cities: [group('İstanbul', 'İstanbul', 100), group('Bilinmiyor', 'Bilinmiyor', 900)],
      geography: { countries: [], previousCities: [group('İstanbul', 'İstanbul', 100), group('Bilinmiyor', 'Bilinmiyor', 1900)], previousCountries: [], abroadOrders: 0 }
    } as unknown as ReportSales
    const { host } = render(<SalesMap data={data} />)
    await act(async () => { await Promise.resolve() })
    const cell = strip(host)
    expect(cell.querySelector('dd')!.textContent).toBe('%0')
    expect(cell.querySelector('small')!.textContent).toMatch(/^Ciro: (.+) → \1$/u)
  })

  it('leaves Turkey and unknown countries out of both periods on the world view', async () => {
    const data = {
      cities: [],
      geography: {
        countries: [group('TR', 'Türkiye', 5000), group('SA', 'Suudi Arabistan', 200)],
        previousCities: [],
        previousCountries: [group('TR', 'Türkiye', 4000), group('SA', 'Suudi Arabistan', 100), group('XX', 'Bilinmiyor', 700)],
        abroadOrders: 1
      }
    } as unknown as ReportSales
    const { host } = render(<SalesMap data={data} />)
    await act(async () => { [...host.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent?.includes('Dünya'))!.click() })
    expect(strip(host).querySelector('dd')!.textContent).toMatch(/100/u)
  })
})
