import { act, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { PriceSuggestionLink } from './PriceSuggestionDialog'
import { suggestPrice } from './price-suggestion'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLElement
let root: Root

beforeEach(() => {
  try { window.localStorage.clear() } catch { /* no storage */ }
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

describe('PriceSuggestionLink', () => {
  it('opens the window from Hesapla and Fiyatı Uygula fills Satış Fiyatı', () => {
    act(() => root.render(<Panel />))
    expect(document.querySelector('.price-suggestion-modal')).toBeNull()
    act(() => button('Hesapla').click())
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
})
