import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return { ...original, hubApi: vi.fn(async (path: string) => { throw new Error(`Test API fixture does not handle ${path}`) }) }
})

import { hubApi } from '../../../shared/api'
import { ProductQuickEditModal } from './ProductQuickEditModal'
import type { Product } from '../shared'
import { testProduct, testVariant } from '../../../test/product'

// Typing in the quick edit table makes drafts only (owner, 18 Nov 2026): no request goes out until Kaydet is pressed and
// confirmed; Tümüne Uygula fills drafts too; Vazgeç and Esc put the saved values back; closing asks first.
const product: Product = testProduct({
  id: 'p1', title: 'Gömlek', updatedAt: '2026-11-01T00:00:00Z', modelCode: 'M1', activePlatforms: [], totalStock: 6,
  variants: [
    testVariant({ id: 'v1', sku: 'SKU-1', modelCode: 'M1', optionSignature: 'Renk:Bej|Beden:S', onHand: 3, available: 3, inventoryVersion: 1, currency: 'TRY', platformStatuses: [], defaultListPrice: 500, defaultSalePrice: 450, version: 1, options: { Renk: 'Bej', Beden: 'S' } }),
    testVariant({ id: 'v2', sku: 'SKU-2', modelCode: 'M1', optionSignature: 'Renk:Bej|Beden:XL', onHand: 3, available: 3, inventoryVersion: 1, currency: 'TRY', platformStatuses: [], defaultListPrice: 500, defaultSalePrice: 450, version: 1, options: { Renk: 'Bej', Beden: 'XL' } })
  ]
})

let container: HTMLDivElement
let root: Root
let closed = 0
const sent: Array<{ path: string; method?: string; body: { onHand?: number; expectedOnHand?: number } }> = []

async function render(mode: 'stock' | 'price' = 'stock') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter><ProductQuickEditModal mode={mode} products={[product]} connections={[]} onChanged={async () => undefined} onClose={() => { closed += 1 }} /></MemoryRouter></QueryClientProvider>)
  })
  for (const toggle of Array.from(document.body.querySelectorAll<HTMLButtonElement>('button.rr-vg-t'))) await click(toggle)
}

async function click(element: HTMLElement) { await act(async () => { element.click() }) }
async function type(input: HTMLInputElement, value: string) {
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}
const stockBox = (sku: string) => document.body.querySelector<HTMLInputElement>(`input[aria-label="${sku} stok"]`)!
const bar = () => document.body.querySelector('.rv-draftbar')
const barButton = (label: string) => Array.from(bar()!.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === label)!
const footButton = (label: string) => Array.from(document.body.querySelectorAll<HTMLButtonElement>('footer button')).find(button => button.textContent === label)!

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  closed = 0
  sent.length = 0
  vi.mocked(hubApi).mockImplementation(async (path: string, init?: RequestInit) => {
    sent.push({ path, method: init?.method, body: init?.body ? JSON.parse(String(init.body)) : {} })
    return {} as never
  })
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  container.remove()
})

describe('ProductQuickEditModal drafts', () => {
  it('writing in a stock box and leaving it sends nothing and shows an unsaved bar', async () => {
    await render()
    expect(bar()).toBeNull()
    const box = stockBox('SKU-1')
    await act(async () => { box.focus() })
    await type(box, '9')
    await act(async () => { box.blur() })
    await act(async () => { box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })) })
    expect(sent).toEqual([])
    expect(box.value).toBe('9')
    expect(bar()?.textContent).toContain('1 Değişiklik')
    expect(box.closest('td')?.classList.contains('rr-draft')).toBe(true)
    expect(box.title).toContain('kayıtlı stok: 3')
  })

  it('Kaydet asks once, then writes one request carrying the stock it replaces', async () => {
    await render()
    await type(stockBox('SKU-1'), '9')
    await click(barButton('Kaydet'))
    expect(sent).toEqual([])
    expect(bar()?.textContent).toContain('1 varyantın stoğu değişecek')
    expect(bar()?.textContent).toContain('Otomatik dış yazması açık pazaryerlerine gönderilir')
    await click(barButton('Onayla ve Kaydet'))
    expect(sent.map(call => [call.path, call.method, call.body.onHand, call.body.expectedOnHand])).toEqual([['/inventory/v1/on-hand', 'PUT', 9, 3]])
    expect(bar()).toBeNull()
  })

  it('Vazgeç puts the saved value back and sends nothing', async () => {
    await render()
    await type(stockBox('SKU-1'), '9')
    await type(stockBox('SKU-2'), '4')
    expect(bar()?.textContent).toContain('2 Değişiklik')
    await click(barButton('Vazgeç'))
    expect(sent).toEqual([])
    expect(stockBox('SKU-1').value).toBe('3')
    expect(stockBox('SKU-2').value).toBe('3')
    expect(bar()).toBeNull()
  })

  it('Esc in one box takes back that box only', async () => {
    await render()
    await type(stockBox('SKU-1'), '9')
    await type(stockBox('SKU-2'), '4')
    await act(async () => { stockBox('SKU-1').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })
    expect(stockBox('SKU-1').value).toBe('3')
    expect(stockBox('SKU-2').value).toBe('4')
    expect(bar()?.textContent).toContain('1 Değişiklik')
    expect(closed).toBe(0)
  })

  it('a draft that fails its check keeps Kaydet off and says why', async () => {
    await render()
    await type(stockBox('SKU-1'), '-2')
    expect(barButton('Kaydet').disabled).toBe(true)
    expect(bar()?.textContent).toContain('sıfır veya pozitif bir tam sayı')
    expect(stockBox('SKU-1').getAttribute('aria-invalid')).toBe('true')
    await click(barButton('Kaydet'))
    expect(sent).toEqual([])
  })

  it('a refused (changed meanwhile) stock stays a draft with its reason', async () => {
    const { ApiRequestError } = await import('../../../shared/api')
    vi.mocked(hubApi).mockImplementation(async (path: string, init?: RequestInit) => {
      sent.push({ path, method: init?.method, body: {} })
      throw new ApiRequestError('Stok başka bir işlemle değişti.', 409, 'STOCK_CHANGED')
    })
    await render()
    await type(stockBox('SKU-1'), '9')
    await click(barButton('Kaydet'))
    await click(barButton('Onayla ve Kaydet'))
    expect(sent).toHaveLength(1)
    expect(stockBox('SKU-1').value).toBe('9')
    expect(stockBox('SKU-1').title).toContain('Stok başka bir işlemle değişti')
    expect(bar()?.textContent).toContain('1 Değişiklik')
  })

  it('closing with a draft asks first; staying keeps it, leaving drops it', async () => {
    await render()
    await type(stockBox('SKU-1'), '9')
    await click(footButton('Kapat'))
    expect(closed).toBe(0)
    expect(bar()?.textContent).toContain('Kaydedilmemiş değişiklikler var')
    await click(barButton('Düzenlemeye Dön'))
    expect(bar()?.textContent).toContain('1 Değişiklik')
    await click(footButton('Kapat'))
    await click(barButton('Değişiklikleri Sil ve Kapat'))
    expect(closed).toBe(1)
    expect(sent).toEqual([])
  })

  it('a panel price typed in a cell is a draft too: nothing goes out until Kaydet is confirmed', async () => {
    await render('price')
    const sale = document.body.querySelector<HTMLInputElement>('input[aria-label="SKU-1 Panel Ana Fiyatı satış fiyatı"], input[aria-label^="SKU-1"][aria-label$="satış fiyatı"]')!
    await act(async () => { sale.focus() })
    await type(sale, '460')
    await act(async () => { sale.blur() })
    expect(sent).toEqual([])
    expect(bar()?.textContent).toContain('1 Değişiklik')
    await click(barButton('Kaydet'))
    expect(sent).toEqual([])
    expect(bar()?.textContent).toContain('1 fiyat hücresi değişecek')
    await click(barButton('Onayla ve Kaydet'))
    expect(sent.some(call => call.path.endsWith('/default-prices') && call.method === 'PATCH')).toBe(true)
  })

  it('closing without a draft closes at once', async () => {
    await render()
    await click(footButton('Vazgeç'))
    expect(closed).toBe(1)
  })
})
