import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Product } from '../shared'
import { LowStockDetailsModal } from './LowStockDetailsModal'

const card = (id: string, model: string) => ({
  id, title: `Örnek Ürün ${model}`, description: '', brandId: 'b1', categoryId: 'c1', status: 'ACTIVE', updatedAt: '2026-11-01T00:00:00Z', version: 1,
  variants: [{ id: `${id}-v1`, sku: `${model}-S`, modelCode: model, optionSignature: 'Renk:Siyah|Beden:S', status: 'ACTIVE', version: 1, onHand: 0, available: 0, salePrice: 450, currency: 'TRY', mediaUrls: [], options: { Renk: 'Siyah', Beden: 'S' }, productId: id }],
  primaryImageUrl: null, familyImageUrl: null, totalStock: 0, startingPrice: 450, currency: 'TRY', modelCode: model, activePlatforms: [], platformStatuses: [], categoryPath: 'Bluz', hasCustomMediaOrder: false
}) as unknown as Product

let container: HTMLDivElement
let root: Root
beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  Element.prototype.scrollIntoView = vi.fn()
  container = document.createElement('div')
  document.body.append(container)
})
afterEach(() => { act(() => root?.unmount()); container.remove() })

const cards = () => [...document.querySelectorAll<HTMLElement>('.rr-lowgrid > .rr-lowf')]
const head = (index: number) => cards()[index].querySelector<HTMLButtonElement>('.rr-lowf-t')!
const settle = () => act(async () => { await new Promise(resolve => setTimeout(resolve, 10)) })

describe('Düşük Stok model cards are an accordion', () => {
  it('one click opens only that model (full row), another click swaps it, the same click closes it', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    root = createRoot(container)
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><LowStockDetailsModal products={[card('a', 'AAA001'), card('b', 'BBB002'), card('c', 'CCC003')]} loading={false} error={null} onClose={() => {}} onImageClick={() => {}} /></MemoryRouter></QueryClientProvider>) })
    expect(cards()).toHaveLength(3)
    expect(document.querySelector('.rr-pair-left, .rr-pair-right, .rr-row-hot')).toBeNull()
    expect(head(0).title).toBe('')

    act(() => head(0).click())
    await settle()
    expect(cards().map(item => item.classList.contains('rr-lowf-full'))).toEqual([true, false, false])
    expect(head(0).getAttribute('aria-expanded')).toBe('true')
    expect(head(1).getAttribute('aria-expanded')).toBe('false')

    act(() => head(1).click())
    await settle()
    expect(cards().map(item => item.classList.contains('rr-lowf-full'))).toEqual([false, true, false])

    act(() => head(1).click())
    await settle()
    expect(cards().some(item => item.classList.contains('rr-lowf-full'))).toBe(false)
  })

  it('an open model closes when the search changes', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    root = createRoot(container)
    await act(async () => { root.render(<QueryClientProvider client={client}><MemoryRouter><LowStockDetailsModal products={[card('a', 'AAA001'), card('b', 'BBB002')]} loading={false} error={null} onClose={() => {}} onImageClick={() => {}} /></MemoryRouter></QueryClientProvider>) })
    act(() => head(0).click())
    await settle()
    expect(cards()[0].classList.contains('rr-lowf-full')).toBe(true)
    const input = document.querySelector<HTMLInputElement>('input[aria-label="Stok eksiklerinde ara"]')!
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, 'AAA')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await settle()
    expect(document.querySelector('.rr-lowf-full')).toBeNull()
  })
})
