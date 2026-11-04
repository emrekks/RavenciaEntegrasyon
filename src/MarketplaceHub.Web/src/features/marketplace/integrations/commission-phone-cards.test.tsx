import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Phones show the tariff's products as cards; a band card is the table cell's pick (the same picks, the same save).
// One tariff: a product of one row ("Tek Ürün", 535 ₺ now, in band 3) and a model of two sizes (M at 625 ₺, XL at 615 ₺)
// whose sizes leave different amounts.
const apiState = vi.hoisted(() => ({ calls: [] as Array<{ path: string; method: string; body?: string }>, rows: [] as unknown[] }))
const costs = { cost: 154, cargo: 98.34, serviceFee: 13.19, stoppage: 4.5, netVat: 10, vatRate: 10 }

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const costs = { cost: 154, cargo: 98.34, serviceFee: 13.19, stoppage: 4.5, netVat: 10, vatRate: 10 }
  const bands = [{ tier: 1, min: 563.84, max: null, rate: 21.5 }, { tier: 2, min: 510.42, max: 563.83, rate: 20.8 }, { tier: 3, min: 457.01, max: 510.41, rate: 19.3 }, { tier: 4, min: null, max: 457, rate: 17.5 }]
  const choice = (tier: number, price: number, remaining: number) => {
    const rate = bands[tier - 1].rate
    return { tier, price, rate, commission: Math.round(price * rate) / 100, remaining, costs }
  }
  const row = (id: string, values: { barcode: string; modelCode: string; productName: string; size: string; color: string | null; price: number; tier: number; profits: number[] }) => ({
    id, barcode: values.barcode, sellerSku: null, modelCode: values.modelCode, productName: values.productName, size: values.size, color: values.color, stock: 5,
    basisPrice: values.price, currentSalePrice: values.price, currentRate: bands[values.tier - 1].rate, currentTier: values.tier, currentBandRate: bands[values.tier - 1].rate,
    bands, nextLowerBand: null, matchesPanel: true, costPrice: 154, imageUrl: null, recommendedTier: 1,
    choices: [choice(1, Math.max(values.price, 563.84), values.profits[0]), choice(2, 563.83, values.profits[1]), choice(3, 510.41, values.profits[2]), choice(4, 457, values.profits[3])]
  })
  const items = [
    row('r1', { barcode: 'B1', modelCode: 'MZ1', productName: 'Tek Ürün', size: 'Std', color: null, price: 535, tier: 2, profits: [120, 110, 100, 80] }),
    row('r2', { barcode: 'B2', modelCode: 'RYP4', productName: 'Penye Bluz', size: 'M', color: 'Lacivert', price: 625, tier: 1, profits: [210, 173.49, 150, 120] }),
    row('r3', { barcode: 'B3', modelCode: 'RYP4', productName: 'Penye Bluz', size: 'XL', color: 'Lacivert', price: 615, tier: 1, profits: [190, 160, 140, 110] })
  ]
  apiState.rows = items
  const tariff = { id: 't1', platformCode: 'TRENDYOL', periodStart: '2026-11-01T05:00:00Z', periodEnd: '2026-11-08T05:00:00Z', periodText: '1 – 8 Kasım', fileName: 'f.xlsx', uploadedAt: '2026-11-01T00:00:00Z',
    rowCount: 3, matchedRowCount: 3, isCurrent: true, periodLabel: null, groupId: null }
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const body = typeof init?.body === 'string' ? init.body : undefined
    apiState.calls.push({ path, method, body })
    if (path.endsWith('/commission-tariffs')) return [tariff]
    if (path.includes('/selection')) return method === 'PUT' ? JSON.parse(body!) : { bulk: null, tier: null, minRate: null, minAmount: null, maxRate: null, maxAmount: null, choices: [] }
    if (path.includes('/facets')) return { categories: [], brands: [] }
    if (path.includes('/rows')) return { items, totalCount: items.length, page: 0, pageSize: 50 }
    throw new Error(`unexpected ${method} ${path}`)
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { CommissionTariffsPanel, ownChoice, type CommissionTariffRow } from './CommissionTariffsPanel'
import { compactProfitText, profitText } from './unit-profit'

const rows = apiState.rows as CommissionTariffRow[]

let container: HTMLDivElement
let root: Root
const desktopMatchMedia = window.matchMedia

function viewport(phone: boolean) {
  window.matchMedia = (query: string) => ({
    matches: query.includes('prefers-reduced-motion: reduce') || (phone && query.includes('max-width: 760px')),
    media: query, onchange: null, addListener: () => undefined, removeListener: () => undefined,
    addEventListener: () => undefined, removeEventListener: () => undefined, dispatchEvent: () => false
  }) as MediaQueryList
}

async function settle(ms = 30) {
  await act(async () => { await new Promise(resolve => setTimeout(resolve, ms)) })
}

async function show(connectionId: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  act(() => root.render(<QueryClientProvider client={client}><CommissionTariffsPanel connectionId={connectionId} platformName="Trendyol" /></QueryClientProvider>))
  await settle()
  await settle()
}

// The last picks the page saved for the tariff.
const savedPicks = () => {
  const saves = apiState.calls.filter(call => call.method === 'PUT' && call.path.includes('/selection'))
  return saves.length ? JSON.parse(saves[saves.length - 1].body!).choices : null
}

const card = (name: string) => [...container.querySelectorAll<HTMLElement>('.commission-card')].find(item => item.querySelector('.commission-card-name')?.textContent === name)!

function type(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  act(() => { setter.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })) })
}

describe('commission tariff phone cards', () => {
  beforeEach(() => {
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    apiState.calls = []
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })
  afterEach(() => { act(() => root.unmount()); container.remove(); window.matchMedia = desktopMatchMedia })

  it('shows each product as a card of bands instead of the table, the current band tagged', async () => {
    viewport(true)
    await show('c-cards')
    expect(container.querySelector('.commission-table')).toBeNull()
    const single = card('Tek Ürün')
    expect(single.querySelector('.commission-card-now')!.textContent).toBe('Güncel 535,00 ₺Şu an %20,8')
    const bands = [...single.querySelectorAll<HTMLElement>('.commission-ruler > .commission-choice')]
    // Four bands in order, then Kendi Fiyatın.
    expect(bands.map(band => band.querySelector('.commission-seg-n')!.textContent)).toEqual(['1. Aralık', '2. Aralık', '3. Aralık', '4. Aralık', 'Kendi Fiyatın'])
    // The band limit as the table's cell heads it, its rate and its profit badge.
    expect(bands[1].querySelector('strong')!.textContent).toBe('563,83 ₺ ve altı')
    expect(bands[1].textContent).toContain('%20,8 komisyon')
    expect(bands[1].querySelector('.unit-profit-badge')!.textContent).toContain(profitText(110, costs))
    expect(bands[1].classList.contains('is-current')).toBe(true)
    expect(bands[1].querySelector('.commission-now-tag')!.textContent).toBe('Şu An')
    expect(single.querySelectorAll('.commission-now-tag')).toHaveLength(1)
    // The Excel bar is on the page with the count.
    expect(container.querySelector('.commission-phone-bar .commission-plan-count')!.textContent).toBe('0 ürün seçildi')
  })

  it('saves the same pick for a band card as the table cell does', async () => {
    viewport(false)
    await show('c-desk')
    const desktopRow = [...container.querySelectorAll('tr')].find(row => row.textContent?.includes('Tek Ürün'))!
    act(() => desktopRow.querySelectorAll<HTMLButtonElement>('.commission-switch')[1].click())
    await settle(700)
    const fromTable = savedPicks()
    expect(fromTable).toEqual([{ rowId: 'r1', tier: 2, price: null }])
    act(() => root.unmount())
    container.remove()

    apiState.calls = []
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    viewport(true)
    await show('c-phone')
    const pick = card('Tek Ürün').querySelector<HTMLButtonElement>('[aria-label="2. Fiyat Aralığı seç"]')!
    act(() => pick.click())
    expect(card('Tek Ürün').querySelectorAll('.commission-ruler > .commission-choice')[1].classList.contains('is-picked')).toBe(true)
    expect(container.querySelector('.commission-phone-bar .commission-plan-count')!.textContent).toBe('1 ürün seçildi')
    await settle(700)
    expect(savedPicks()).toEqual(fromTable)
    expect(apiState.calls.find(call => call.method === 'PUT')!.path).toBe('/connections/c-phone/commission-tariffs/t1/selection')
  })

  it('keeps the desktop table narrow: Stok under the name, a short profit badge and switch', async () => {
    viewport(false)
    await show('c-narrow')
    expect([...container.querySelectorAll('thead th')].map(th => th.textContent)).toEqual(['', 'Ürün↕', 'Güncel Fiyat↕', '1. Fiyat Aralığı↕', '2. Fiyat Aralığı↕', '3. Fiyat Aralığı↕', '4. Fiyat Aralığı↕', 'Kendi Fiyatın'])
    const single = [...container.querySelectorAll('tr')].find(row => row.textContent?.includes('Tek Ürün'))!
    expect(single.querySelector('.commission-stock-line')!.textContent).toBe('Stok 5')
    const badge = single.querySelectorAll('.commission-choice')[1].querySelector('.unit-profit-badge')!
    expect(badge.textContent).toBe(`${compactProfitText(110, costs)}›`)
    expect(badge.getAttribute('aria-label')).toBe(profitText(110, costs))
    const model = [...container.querySelectorAll('tr.is-model')].find(row => row.textContent?.includes('Penye Bluz'))!
    expect(model.querySelector('.commission-switch')!.textContent).toBe('Tüm Varyantlar')
  })

  it('picks a band for all of a model\'s sizes from the model card', async () => {
    viewport(true)
    await show('c-model')
    act(() => card('Penye Bluz').querySelector<HTMLButtonElement>('[aria-label="2. Fiyat Aralığı: tüm varyantlarını seç"]')!.click())
    await settle(700)
    expect(savedPicks()).toEqual([{ rowId: 'r2', tier: 2, price: null }, { rowId: 'r3', tier: 2, price: null }])
    expect(card('Penye Bluz').querySelector('[aria-label="2. Fiyat Aralığı: tüm varyantlar seçili"]')!.textContent).toBe('Tümü Seçili')
  })

  it('works out the band and profit of a price typed in Kendi Fiyatın and saves it as the pick', async () => {
    viewport(true)
    await show('c-own')
    const own = card('Tek Ürün').querySelector<HTMLInputElement>('.commission-own-card input')!
    type(own, '500')
    const shown = card('Tek Ürün').querySelector('.commission-own-card')!
    // 500 ₺ is in band 3 (457,01 – 510,41) at %19,3: 96,50 ₺ of commission.
    expect(shown.querySelector('small')!.textContent).toContain('3. fiyat aralığı · %19,3')
    // Its profit as Kendi Fiyatın works it out (ownChoice), shown short with the full text as the badge's name.
    const expected = ownChoice(rows[0], 500)!
    expect(shown.querySelector('.unit-profit-badge')!.getAttribute('aria-label')).toBe(profitText(expected.remaining!, expected.costs))
    expect(shown.querySelector('.unit-profit-badge')!.textContent).toBe(`${compactProfitText(expected.remaining!, expected.costs)}›`)
    await settle(700)
    expect(savedPicks()).toEqual([{ rowId: 'r1', tier: null, price: 500 }])
  })

  it('opens a model\'s sizes, each with its own price and profits', async () => {
    viewport(true)
    await show('c-sizes')
    const model = card('Penye Bluz')
    expect(model.querySelector('.commission-sizes')).toBeNull()
    const toggle = model.querySelector<HTMLButtonElement>('.commission-sizes-toggle')!
    expect(toggle.textContent).toBe('2 Bedeni Ayrı Seç')
    act(() => toggle.click())
    const sizes = [...card('Penye Bluz').querySelectorAll<HTMLElement>('.commission-size')]
    expect(sizes.map(size => size.querySelector('.commission-size-head')!.textContent)).toEqual(['M625,00 ₺ · Stok 5', 'XL615,00 ₺ · Stok 5'])
    const secondBand = (size: HTMLElement) => size.querySelectorAll('.commission-ruler > .commission-choice')[1].querySelector('.unit-profit-badge')!.textContent
    expect(secondBand(sizes[0])).toContain(profitText(173.49, costs))
    expect(secondBand(sizes[1])).toContain(profitText(160, costs))
    expect(card('Penye Bluz').querySelector('.commission-sizes-toggle')!.textContent).toBe('Bedenleri Gizle')
    // A size's band picks that size alone.
    act(() => sizes[1].querySelector<HTMLButtonElement>('[aria-label="4. Fiyat Aralığı seç"]')!.click())
    await settle(700)
    expect(savedPicks()).toEqual([{ rowId: 'r3', tier: 4, price: null }])
  })
})
