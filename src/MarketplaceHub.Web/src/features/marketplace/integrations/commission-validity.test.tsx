import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// One file of two periods (3 Gün and 4 Gün): the days it covers are 29 Ekim – 5 Kasım in Istanbul.
const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string }> }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const tariff = (id: string, start: string, end: string, label: string) => ({ id, platformCode: 'TRENDYOL', periodStart: start, periodEnd: end, periodText: label, fileName: 'f.xlsx', uploadedAt: '2026-10-31T00:00:00Z',
    rowCount: 1, matchedRowCount: 1, isCurrent: false, periodLabel: label, groupId: 't3' })
  const tariffs = [tariff('t3', '2026-10-29T05:00:00Z', '2026-11-01T05:00:00Z', '3 Gün'), tariff('t4', '2026-11-01T05:00:00Z', '2026-11-05T05:00:00Z', '4 Gün')]
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    apiState.calls.push({ path, method, body: typeof init?.body === 'string' ? init.body : undefined })
    if (path.endsWith('/commission-tariffs')) return tariffs
    if (path.endsWith('/period') && method === 'PUT') return tariffs
    if (path.includes('/selection')) return { bulk: null, tier: null, minRate: null, minAmount: null, maxRate: null, maxAmount: null, choices: [] }
    if (path.includes('/facets')) return { categories: [], brands: [] }
    if (path.includes('/rows')) return { items: [], totalCount: 0, page: 0, pageSize: 50 }
    throw new Error(`unexpected ${method} ${path}`)
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { CommissionTariffsPanel, validityDays, validityProblem, type CommissionTariff } from './CommissionTariffsPanel'

let container: HTMLDivElement
let root: Root

async function settle(ms = 30) {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, ms)) })
}

// A date field typed into as a person would (React reads the native value setter).
function type(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  act(() => { setter.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })) })
}

describe('commission tariff validity days', () => {
  beforeEach(() => {
    apiState.calls = []
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })
  afterEach(() => { act(() => root.unmount()); container.remove() })

  it('reads a file\'s Istanbul days and tells what is missing', () => {
    const periods = [{ periodStart: '2026-10-29T05:00:00Z', periodEnd: '2026-11-01T05:00:00Z' }, { periodStart: '2026-11-01T05:00:00Z', periodEnd: '2026-11-05T05:00:00Z' }] as CommissionTariff[]
    expect(validityDays(periods)).toEqual({ from: '2026-10-29', to: '2026-11-05' })
    // A period ending at Istanbul midnight ends on the day before.
    expect(validityDays([{ periodStart: '2026-11-01T21:00:00Z', periodEnd: '2026-11-08T21:00:00Z' }] as CommissionTariff[])).toEqual({ from: '2026-11-02', to: '2026-11-08' })
    expect(validityProblem({ from: '', to: '2026-11-05' })).toBe('Önce geçerlilik başlangıcı ve bitişini seçin.')
    expect(validityProblem({ from: '2026-11-06', to: '2026-11-05' })).toBe('Geçerlilik başlangıcı, bitişten sonra olamaz.')
    expect(validityProblem({ from: '2026-11-05', to: '2026-11-05' })).toBeNull()
  })

  it('keeps the file button disabled until both validity days are picked', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    act(() => root.render(<QueryClientProvider client={client}><CommissionTariffsPanel connectionId="c1" platformName="Trendyol" /></QueryClientProvider>))
    await settle()
    const zone = container.querySelector<HTMLElement>('.commission-dropzone')!
    const pick = [...zone.querySelectorAll('button')].find(button => button.textContent === 'Dosya Seç')!
    expect(pick.disabled).toBe(true)
    expect(zone.querySelector('.commission-validity-hint')?.textContent).toBe('Önce geçerlilik başlangıcı ve bitişini seçin.')
    const [from, to] = [...zone.querySelectorAll<HTMLInputElement>('input[type="date"]')]
    expect([...zone.querySelectorAll('label span')].map(label => label.textContent)).toEqual(['Geçerlilik Başlangıcı', 'Geçerlilik Bitişi'])
    type(from, '2026-11-02')
    expect(pick.disabled).toBe(true)
    type(to, '2026-11-01')
    expect(pick.disabled).toBe(true)
    expect(zone.querySelector('.commission-validity-hint')?.textContent).toBe('Geçerlilik başlangıcı, bitişten sonra olamaz.')
    type(to, '2026-11-08')
    expect(pick.disabled).toBe(false)
    expect(zone.querySelector('.commission-validity-hint')).toBeNull()
  })

  it('changes a file\'s days from its card and refreshes the tariffs and the reports', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    client.setQueryData(['report-order-profit', 'q'], { items: [] })
    act(() => root.render(<QueryClientProvider client={client}><CommissionTariffsPanel connectionId="c1" platformName="Trendyol" /></QueryClientProvider>))
    await settle()
    const card = container.querySelector<HTMLElement>('.commission-period')!
    act(() => [...card.querySelectorAll('button')].find(button => button.textContent === 'Tarihleri Değiştir')!.click())
    const form = card.querySelector<HTMLFormElement>('.commission-period-edit')!
    const [from, to] = [...form.querySelectorAll<HTMLInputElement>('input[type="date"]')]
    // The fields open on the days the file covers now.
    expect([from.value, to.value]).toEqual(['2026-10-29', '2026-11-05'])
    type(from, '2026-10-28')
    type(to, '2026-11-09')
    const listed = apiState.calls.filter(call => call.path.endsWith('/commission-tariffs')).length
    act(() => [...form.querySelectorAll('button')].find(button => button.textContent === 'Kaydet')!.click())
    await settle()
    const put = apiState.calls.filter(call => call.method === 'PUT' && call.path.endsWith('/period'))
    expect(put.map(call => call.path)).toEqual(['/connections/c1/commission-tariffs/t3/period'])
    expect(JSON.parse(put[0].body!)).toEqual({ validFrom: '2026-10-28', validTo: '2026-11-09', wholeFile: true })
    expect(apiState.calls.filter(call => call.path.endsWith('/commission-tariffs')).length).toBeGreaterThan(listed)
    expect(client.getQueryState(['report-order-profit', 'q'])?.isInvalidated).toBe(true)
    expect(container.querySelector('.commission-period-edit')).toBeNull()
    expect(container.querySelector('.commission-message')?.textContent).toBe('Tarifenin geçerlilik tarihleri değiştirildi.')
  })
})
