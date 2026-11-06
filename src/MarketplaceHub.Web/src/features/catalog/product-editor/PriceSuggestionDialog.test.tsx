import { act, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const apiState = vi.hoisted(() => ({ paths: [] as string[] }))

// The hints are static text: the dialog must not ask the API for anything.
vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return { ...original, hubApi: vi.fn(async (path: string) => { apiState.paths.push(path); throw new Error('the dialog must not call the API') }) }
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

  it('puts an "i" beside Komisyon, Kargo and Hizmet Bedeli with the published women\'s clothing figures, without calling the API', async () => {
    await open()
    expect(apiState.paths).toEqual([])
    // Each input keeps its label's text as its name: the hint sits beside the label, not inside it.
    for (const label of ['Komisyon Oranı (%)', 'Kargo Ücreti (₺)', 'Hizmet Bedeli (₺)']) {
      const field = [...document.querySelectorAll('label')].find(item => item.textContent === label)!
      expect(document.getElementById(field.htmlFor)?.tagName).toBe('INPUT')
      expect(document.querySelector(`button.title-hint[aria-label="${label} hakkında"] .ui-icon-infoCircle`)).not.toBeNull()
    }

    const commission = hint('Komisyon Oranı (%)')
    expect(commission).toContain('Trendyol %21,5 (KDV dahil; kaynaklar %21,36–%22,5 arası)')
    expect(commission).toContain('Hepsiburada %18 + %20 KDV (kutuya %21,6 girin)')
    expect(commission).toContain('n11 %20 (KDV dahil; kaynak Aralık 2024)')
    expect(commission).toContain('Pazarama kategori oranı bulunamadı; genel aralık %0–19, üstüne %20 KDV')
    expect(commission).toContain('PTT AVM kategori oranı bulunamadı; genel aralık %5–15')
    expect(commission).toContain('Shopify pazaryeri komisyonu yok; kart: iyzico %4,29 + 0,25 ₺ ve Shopify işlem ücreti %2 (toplam ≈ %6,29 + 0,25 ₺)')
    expect(commission).toContain('Kesin oran mağaza panelindeki kategori tablosundadır.')
    expect(commission).toContain('Kaynak: Trendyol / Hepsiburada / n11 komisyon: Ideasoft & Paraşüt (Eki 2026); kargo: pazaryeri kargo listeleri (Eki 2026); ayrıntı docs/KARGO-TARIFELERI-2026.md')

    const cargo = hint('Kargo Ücreti (₺)')
    expect(cargo).toContain('Trendyol 0–2 desi 81,95 ₺ (KDV dahil 98,34 ₺), 3 desi 100,20 ₺')
    expect(cargo).toContain('Hepsiburada hepsiJET 0–2 desi 81,99 ₺, 3 desi 99,99 ₺')
    expect(cargo).toContain('n11 PTT 0–2 desi 81,82 ₺, 3 desi 101,29 ₺ (+%2,35 PHB)')
    expect(cargo).toContain('Pazarama Aras 1 desi 91,46 ₺, 2 desi 101,36 ₺, 3 desi 113,34 ₺ (+%2,35 PHB)')
    expect(cargo).toContain('PTT AVM 1–2 desi 81,25 ₺ KDV dahil')
    expect(cargo).toContain('Shopify kendi anlaşmanız (≈120 ₺)')

    const fee = hint('Hizmet Bedeli (₺)')
    expect(fee).toContain('Trendyol 10,99 + KDV = 13,19 ₺ / paket')
    expect(fee).toContain('Hepsiburada 10,99 + KDV = 13,19 ₺ / teslimat (21 Eylül 2026\'dan beri)')
    expect(fee).toContain('n11 sabit ücret yok; pazarlama %1 + pazaryeri %0,67, KDV üstüne')
    expect(fee).toContain('Pazarama 6,90 + KDV = 8,28 ₺ / sipariş')
    expect(fee).toContain('PTT AVM ödeme altyapı bedeli %1,5')
    expect(fee).toContain('Shopify yok')
  })
})
