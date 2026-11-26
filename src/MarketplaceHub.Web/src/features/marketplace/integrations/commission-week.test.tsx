import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Trendyol's week: 3 Gün and 4 Gün with the same band limits and their own rates; each period's rows have ids of their own.
// A test may hold the second period's rows back (hold) to look at the view before they arrive.
const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string }>, hold: null as Promise<void> | null }))

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
    const { compactRowPage } = await import('./commission-tariff-rows.test-util')
    if (path.includes('/t3/rows')) return compactRowPage({ items: [row('r3', [21.5, 20.6, 19.6, 17.5], [90, 100, 80, 60])], totalCount: 1, page: 0, pageSize: 50 })
    if (path.includes('/t4/rows') && apiState.hold) await apiState.hold
    if (path.includes('/t4/rows')) return compactRowPage({ items: [row('r4', [21.5, 15.8, 14.4, 12], [90, 135, 110, 85])], totalCount: 1, page: 0, pageSize: 200 })
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

// Waits for what the page must come to show instead of a fixed time: the 7-day view reads the second period's rows only
// after the first period's page and both selections, a chain a slow CI runner does not finish in 30 ms. Gives up after
// 5 s and leaves the failing assertion to say what is missing.
async function until(ready: () => boolean) {
  for (let waited = 0; !ready() && waited < 5000; waited += 10) await settle(10)
}

const weekLines = (card: Element) => [...card.querySelectorAll('.commission-week-line > em')].map(line => line.textContent)
const variantCard = () => container.querySelectorAll<HTMLElement>('tbody .commission-choice')[1]
const saved = (id: string) => apiState.calls.filter(call => call.method === 'PUT' && call.path.includes(`/${id}/selection`)).map(call => JSON.parse(call.body!).choices)

async function openWeek() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  act(() => root.render(<QueryClientProvider client={client}><CommissionTariffsPanel connectionId="c1" platformName="Trendyol" /></QueryClientProvider>))
  await until(() => container.querySelectorAll('.commission-period-tabs button').length === 3)
  const tabs = [...container.querySelectorAll<HTMLButtonElement>('.commission-period-tabs button')]
  expect(tabs.map(tab => tab.textContent)).toEqual(['3 Gün (29 Eki – 1 Kas 2026)Şu An', '4 Gün (1 Kas – 5 Kas 2026)', '7 Gün (29 Eki – 5 Kas 2026)'])
  act(() => tabs[2].click())
}

describe('commission tariff 7-day view', () => {
  beforeEach(() => {
    apiState.calls = []
    apiState.hold = null
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })
  afterEach(() => { act(() => root.unmount()); container.remove() })

  it('shows both periods beside each band with the week\'s average, and a pick there is saved for both', async () => {
    await openWeek()
    await until(() => variantCard() !== undefined && weekLines(variantCard()).length === 2 && !variantCard().textContent!.includes('Yükleniyor'))

    // The second period's rows are read by the page's barcodes.
    expect(apiState.calls.some(call => call.path.includes('/t4/rows?') && call.path.includes('barcodes=RYP00101'))).toBe(true)
    const card = variantCard()
    expect(weekLines(card)).toEqual(['3 Gün · %20,6', '4 Gün · %15,8'])
    // (3 × 100 + 4 × 135) / 7 = 120.
    expect(card.querySelector('.commission-week-average')?.textContent).toBe('7 Gün ortalaması: 120,00 ₺ Kâr')

    act(() => card.querySelector<HTMLButtonElement>('.commission-switch')!.click())
    await until(() => saved('t3').length > 0 && saved('t4').length > 0)
    expect(saved('t3').at(-1)).toEqual([{ rowId: 'r3', tier: 2, price: null }])
    expect(saved('t4').at(-1)).toEqual([{ rowId: 'r4', tier: 2, price: null }])
  })

  it("takes no pick before the second period's rows are in, so a pick never reaches one period only", async () => {
    let release = () => {}
    apiState.hold = new Promise<void>(resolve => { release = resolve })
    await openWeek()
    await until(() => variantCard()?.textContent?.includes('Yükleniyor') ?? false)
    const card = variantCard()
    // The second period's rate is not known yet: the card says so and shows no week average.
    expect(weekLines(card)).toEqual(['3 Gün · %20,6', '4 Gün'])
    expect(card.querySelector('.commission-week-average')?.textContent).toBe('7 Gün ortalaması: hesaplanamadı')
    const pick = card.querySelector<HTMLButtonElement>('.commission-switch')!
    expect(pick.disabled).toBe(true)
    act(() => pick.click())

    act(() => release())
    await until(() => !variantCard().textContent!.includes('Yükleniyor'))
    expect(weekLines(variantCard())).toEqual(['3 Gün · %20,6', '4 Gün · %15,8'])
    expect(variantCard().classList.contains('is-picked')).toBe(false)
    act(() => variantCard().querySelector<HTMLButtonElement>('.commission-switch')!.click())
    await until(() => saved('t4').length > 0)
    expect(saved('t3').at(-1)).toEqual([{ rowId: 'r3', tier: 2, price: null }])
    expect(saved('t4').at(-1)).toEqual([{ rowId: 'r4', tier: 2, price: null }])
  })
})
