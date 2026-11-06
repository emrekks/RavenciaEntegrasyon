import { act, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { PriceAssumptionStore, PriceAssumptions } from './price-assumptions'

const apiState = vi.hoisted(() => ({ paths: [] as string[], fail: false }))

// What the server measured on live (6 Nov 2026, 8 Sep - 6 Nov): the hint must show these, not numbers of its own.
const store = (values: Partial<PriceAssumptionStore> & Pick<PriceAssumptionStore, 'platformCode'>): PriceAssumptionStore => ({
  connectionId: values.platformCode, displayName: values.platformCode, orders: 0, sales: 0, commissionRate: null, commissionRateBeforeVat: null, takesCommission: true,
  realCommissionOrders: 0, cargo: null, cargoSource: null, serviceFee: 0, serviceFeeRate: 0, serviceFeeSource: 'tariff', serviceFeeOrders: 0, azerbaijanServiceFee: null,
  internationalServiceFeeRate: null, paymentFeeRate: null, paymentFeeFixed: null, ...values
})
const measured: PriceAssumptions = {
  from: '2026-09-08', to: '2026-11-06', days: 60, clothingOnly: true, minimumOrders: 5,
  stores: [
    store({ platformCode: 'TRENDYOL', orders: 907, commissionRate: 20, cargo: 98.34, cargoSource: 'invoice', serviceFee: 13.19, azerbaijanServiceFee: 10.99, internationalServiceFeeRate: 6 }),
    store({ platformCode: 'HEPSIBURADA', orders: 30, commissionRate: 21.6, commissionRateBeforeVat: 18, cargo: 98.39, cargoSource: 'tariff', serviceFee: 7.63, serviceFeeSource: 'records', serviceFeeOrders: 27 }),
    store({ platformCode: 'N11', orders: 8, commissionRate: 20.62, cargo: 111.15, cargoSource: 'tariff', serviceFeeRate: 2 }),
    store({ platformCode: 'PAZARAMA', orders: 4, cargo: 112.33, cargoSource: 'tariff', serviceFee: 8.28 }),
    store({ platformCode: 'PTTAVM', orders: 2, serviceFeeRate: 1.5 }),
    store({ platformCode: 'SHOPIFY', takesCommission: false, commissionRate: 0, cargo: 120, cargoSource: 'assumption', paymentFeeRate: 6.29, paymentFeeFixed: 0.25 })
  ]
}

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return { ...original, hubApi: vi.fn(async (path: string) => {
    apiState.paths.push(path)
    if (apiState.fail) throw new Error('down')
    return measured
  }) }
})

import { PriceSuggestionLink } from './PriceSuggestionDialog'
import { suggestPrice } from './price-suggestion'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLElement
let root: Root
let client: QueryClient

beforeEach(() => {
  try { window.localStorage.clear() } catch { /* no storage */ }
  apiState.paths = []
  apiState.fail = false
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  container = document.createElement('main')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

// The pricing card's fields as the editor holds them.
function Panel() {
  const [sale, setSale] = useState('')
  const [list, setList] = useState('')
  return <section>
    <PriceSuggestionLink fields={{ cost: '200', vatRate: '20', vatIncluded: true, listPrice: list }} onApply={prices => { setSale(prices.salePrice); setList(prices.listPrice) }} />
    <label>Liste Fiyatı<input aria-label="Liste Fiyatı" value={list} readOnly /></label>
    <label>Satış Fiyatı<input aria-label="Satış Fiyatı" value={sale} readOnly /></label>
  </section>
}

const button = (text: string) => [...document.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent === text)!
const settle = () => act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })
const open = async () => {
  act(() => root.render(<QueryClientProvider client={client}><Panel /></QueryClientProvider>))
  act(() => button('Hesapla').click())
  await settle()
}
// The card a field's "i" opens, after a tap on it.
function hint(label: string) {
  const mark = document.querySelector<HTMLButtonElement>(`button.title-hint[aria-label="${label} hakkında"]`)!
  expect(mark).not.toBeNull()
  act(() => mark.click())
  const tip = document.getElementById(mark.getAttribute('aria-describedby')!)!
  expect(tip.hidden).toBe(false)
  const text = tip.textContent ?? ''
  act(() => { document.body.dispatchEvent(new Event('pointerdown', { bubbles: true })) })
  return text
}

describe('PriceSuggestionLink', () => {
  it('opens the window from Hesapla and Fiyatı Uygula fills Satış Fiyatı', async () => {
    await open()
    const modal = document.querySelector('.price-suggestion-modal')!
    expect(modal).not.toBeNull()
    expect(modal.querySelector<HTMLInputElement>('input')!.value).toBe('200')
    expect(button('Maliyete Göre').getAttribute('aria-pressed')).toBe('true')
    act(() => button('Fiyatı Uygula').click())
    expect(document.querySelector('.price-suggestion-modal')).toBeNull()
    const expected = String(suggestPrice({ cost: 200, targetRate: 50, basis: 'cost', commissionRate: 20, cargo: 0, serviceFee: 13.19, vatRate: 20, vatIncluded: true })!.price)
    expect(container.querySelector<HTMLInputElement>('input[aria-label="Satış Fiyatı"]')!.value).toBe(expected)
    expect(container.querySelector<HTMLInputElement>('input[aria-label="Liste Fiyatı"]')!.value).toBe(expected)
  })

  it('puts an "i" beside Komisyon, Kargo and Hizmet Bedeli with what each marketplace took of the clothing sales', async () => {
    await open()
    expect(apiState.paths).toEqual(['/reports/price-assumptions'])
    // Each input keeps its label's text as its name: the hint sits beside the label, not inside it.
    for (const label of ['Komisyon Oranı (%)', 'Kargo Ücreti (₺)', 'Hizmet Bedeli (₺)']) {
      const field = [...document.querySelectorAll('label')].find(item => item.textContent === label)!
      expect(document.getElementById(field.htmlFor)?.tagName).toBe('INPUT')
      expect(document.querySelector(`button.title-hint[aria-label="${label} hakkında"] .ui-icon-infoCircle`)).not.toBeNull()
    }

    const commission = hint('Komisyon Oranı (%)')
    expect(commission).toContain('Trendyol %20,00 (907 satış)')
    expect(commission).toContain('Hepsiburada %18,00 + %20 KDV → %21,60 girin (30 satış)')
    expect(commission).toContain('n11 %20,62 (8 satış)')
    expect(commission).toContain('Pazarama bilinmiyor: 4 satış, oran için en az 5 gerekir')
    expect(commission).toContain('PTT AVM bilinmiyor: 2 satış')
    expect(commission).toContain('Shopify pazaryeri komisyonu yok; kartlı ödeme kesintisi %6,29 + 0,25 ₺')
    expect(commission).toContain('Son 60 gün (8 Eylül – 6 Kasım), giyim satışları')

    const cargo = hint('Kargo Ücreti (₺)')
    expect(cargo).toContain('Trendyol 98,34 ₺ (kargo faturalarındaki güncel tarife)')
    expect(cargo).toContain('Hepsiburada 98,39 ₺ (pazaryerinin 2026 tarifesi, en sık tutar)')
    expect(cargo).toContain('PTT AVM bilinmiyor')
    expect(cargo).toContain('Shopify 120,00 ₺ (varsayım)')

    const fee = hint('Hizmet Bedeli (₺)')
    expect(fee).toContain('Trendyol 13,19 ₺; Azerbaycan vitrininde 10,99 ₺; yurt dışı vitrinlerde + satıştan %6,00')
    expect(fee).toContain('Hepsiburada 7,63 ₺ (muhasebe kayıtlarında 27 siparişin ortalaması)')
    expect(fee).toContain('n11 satıştan %2,00')
    expect(fee).toContain('Pazarama 8,28 ₺')
    expect(fee).toContain('Shopify yok')
  })

  it('says so when the figures cannot be read, and says all sales when clothing cannot be told apart', async () => {
    apiState.fail = true
    await open()
    expect(hint('Komisyon Oranı (%)')).toBe('Pazaryeri değerleri alınamadı.')

    act(() => root.unmount())
    root = createRoot(container)
    apiState.fail = false
    client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    measured.clothingOnly = false
    try {
      await open()
      expect(hint('Kargo Ücreti (₺)')).toContain('giyim kategorileri ayırt edilemedi, tüm satışlar')
    } finally {
      measured.clothingOnly = true
    }
  })
})
