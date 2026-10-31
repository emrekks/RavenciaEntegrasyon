import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Trendyol's week: 3 Gün and 4 Gün with the same band limits and their own rates; each period's rows have ids of their own.
const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string }> }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const band = (tier: number, min: number | null, max: number | null, rate: number) => ({ tier, min, max, rate })
  const costs = { cost: 150, cargo: 50, serviceFee: 13.19, stoppage: 4, netVat: 10, vatRate: 10 }
  const row = (id: string, rates: number[], lefts: number[]) => ({
    id, barcode: 'RYP00101', sellerSku: null, modelCode: null, productName: 'Penye Bluz', size: 'S', basisPrice: 519.75, currentSalePrice: 519.75, currentRate: rates[1], currentTier: 2, currentBandRate: rates[1],
    bands: [band(1, 563.84, null, rates[0]), band(2, 510.42, 563.83, rates[1]), band(3, 457.01, 510.41, rates[2]), band(4, null, 457, rates[3])], nextLowerBand: null, matchesPanel: true, costPrice: 150,
    choices: [1, 2, 3, 4].map((tier, index) => ({ tier, price: [563.84, 519.75, 510.41, 457][index], rate: rates[index], commission: 10, remaining: lefts[index], costs })), stock: 4
  })
  const tariff = (id: string, start: string, end: string, label: string) => ({ id, platformCode: 'TRENDYOL', periodStart: start, periodEnd: end, periodText: label, fileName: 'f.xlsx', uploadedAt: '2026-10-31T00:00:00Z',
    rowCount: 1, matchedRowCount: 1, isCurrent: id === 't3', periodLabel: label, groupId: 't3' })
  const empty = { bulk: null, tier: null, minRate: null, minAmount: null, maxRate: null, maxAmount: null, choices: [] }
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    apiState.calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : undefined })
    if (path.endsWith('/commission-tariffs')) return [tariff('t3', '2026-10-29T05:00:00Z', '2026-11-01T05:00:00Z', '3 Gün'), tariff('t4', '2026-11-01T05:00:00Z', '2026-11-05T05:00:00Z', '4 Gün')]
    if (path.includes('/selection')) return method === 'PUT' ? JSON.parse(String(init?.body)) : empty
    if (path.includes('/facets')) return { categories: [], brands: [] }
    if (path.includes('/t3/rows')) return { items: [row('r3', [21.5, 20.6, 19.6, 17.5], [90, 100, 80, 60])], totalCount: 1, page: 0, pageSize: 50 }
    if (path.includes('/t4/rows')) return { items: [row('r4', [21.5, 15.8, 14.4, 12], [90, 135, 110, 85])], totalCount: 1, page: 0, pageSize: 200 }
    throw new Error(`unexpected ${method} ${path}`)
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { CommissionTariffsPanel } from './CommissionTariffsPanel'

let container: HTMLDivElement
let root: Root

async function settle(ms = 30) {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, ms)) })
}

describe('commission tariff 7-day view', () => {
  beforeEach(() => {
    apiState.calls = []
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })
  afterEach(() => { act(() => root.unmount()); container.remove() })

  it('shows both periods beside each band with the week\'s average, and a pick there is saved for both', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    act(() => root.render(<QueryClientProvider client={client}><CommissionTariffsPanel connectionId="c1" platformName="Trendyol" /></QueryClientProvider>))
    await settle()
    const tabs = [...container.querySelectorAll<HTMLButtonElement>('.commission-period-tabs button')]
    expect(tabs.map(tab => tab.textContent)).toEqual(['3 Gün (29 Eki – 1 Kas 2026)Şu An', '4 Gün (1 Kas – 5 Kas 2026)', '7 Gün (29 Eki – 5 Kas 2026)'])
    act(() => tabs[2].click())
    await settle()

    // The second period's rows are read by the page's barcodes.
    expect(apiState.calls.some(call => call.path.includes('/t4/rows?') && call.path.includes('barcodes=RYP00101'))).toBe(true)
    const card = container.querySelectorAll<HTMLElement>('tbody .commission-choice')[1]
    expect([...card.querySelectorAll('.commission-week-line > em')].map(line => line.textContent)).toEqual(['3 Gün · %20,6', '4 Gün · %15,8'])
    // (3 × 100 + 4 × 135) / 7 = 120.
    expect(card.querySelector('.commission-week-average')?.textContent).toBe('7 Gün ortalaması: 120,00 ₺ Kâr')

    act(() => card.querySelector<HTMLButtonElement>('.commission-switch')!.click())
    await settle(700)
    const saved = (id: string) => apiState.calls.filter(call => call.method === 'PUT' && call.path.includes(`/${id}/selection`)).map(call => JSON.parse(call.body!).choices)
    expect(saved('t3').at(-1)).toEqual([{ rowId: 'r3', tier: 2, price: null }])
    expect(saved('t4').at(-1)).toEqual([{ rowId: 'r4', tier: 2, price: null }])
  })
})
