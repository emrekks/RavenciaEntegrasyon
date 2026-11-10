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
    const expected = String(suggestPrice({ cost: 200, targetRate: 65, basis: 'cost', commissionRate: 21.5, cargo: 100, serviceFee: 13.19, vatRate: 20, vatIncluded: true })!.price)
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
    expect(commission).toContain('Trendyol %21,5')
    expect(commission).toContain('Hepsiburada %21,6')
    expect(commission).toContain('N11 %20')
    expect(commission).toContain('Pazarama %19')
    expect(commission).toContain('PttAVM %15')
    expect(commission).toContain('Shopify %6,29')

    const cargo = hint('Kargo Ücreti (₺)')
    expect(cargo).toContain('Trendyol 98,34 ₺')
    expect(cargo).toContain('Hepsiburada 98,39 ₺')
    expect(cargo).toContain('N11 100,50 ₺')
    expect(cargo).toContain('Pazarama 112,33 ₺')
    expect(cargo).toContain('PttAVM 81,25 ₺')
    expect(cargo).toContain('Shopify 120 ₺')

    const fee = hint('Hizmet Bedeli (₺)')
    expect(fee).toContain('Trendyol 13,19 ₺')
    expect(fee).toContain('Hepsiburada 13,19 ₺')
    expect(fee).toContain('N11 %2')
    expect(fee).toContain('Pazarama 8,28 ₺')
    expect(fee).toContain('PttAVM %1,5')
    expect(fee).toContain('Shopify 0 ₺')
  })
})

describe('PriceSuggestionDialog defaults', () => {
  it('opens with 21,5 % commission, 100 TL cargo, 65 % target rate and a decimal comma that can be typed', async () => {
    await open()
    const modal = document.querySelector('.price-suggestion-modal')!
    const value = (label: string) => (document.getElementById([...modal.querySelectorAll('label')].find(item => item.textContent === label)!.htmlFor) as HTMLInputElement).value
    expect(value('Komisyon Oranı (%)')).toBe('21,5')
    expect(value('Kargo Ücreti (₺)')).toBe('100')
    expect(value('Hizmet Bedeli (₺)')).toBe('13,19')
    expect([...modal.querySelectorAll('input')].every(input => input.type === 'text')).toBe(true)
    expect([...modal.querySelectorAll('input')].some(input => input.value === '65')).toBe(true)
  })
})
