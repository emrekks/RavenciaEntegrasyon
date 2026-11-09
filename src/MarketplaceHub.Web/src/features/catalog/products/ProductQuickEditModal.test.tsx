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
import { ProductQuickEditModal } from './ProductsPage'
import type { Product, Variant } from '../shared'

function variant(id: string, color: string, size: string): Variant {
  return {
    id, version: 1, sku: `SKU-${id}`, barcode: null, modelCode: 'M1', optionSignature: `Renk:${color}|Beden:${size}`, status: 'ACTIVE',
    weight: null, width: null, height: null, length: null, desi: null, costPrice: null, onHand: 3, available: 3, inventoryVersion: 1,
    offerId: null, listPrice: null, salePrice: null, currency: 'TRY', offerStatus: null, priceVersion: null, offerVersion: null,
    vatRate: null, vatInclusion: null, roundingMode: null, safetyStock: null, options: { Renk: color, Beden: size }
  }
}

const product = {
  id: 'p1', version: 1, title: 'Gömlek', description: '', brandId: null, categoryId: null, status: 'ACTIVE', updatedAt: '2026-11-01T00:00:00Z',
  variants: [variant('b-s', 'Bej', 'S'), variant('b-xl', 'Bej', 'XL'), variant('s-s', 'Siyah', 'S'), variant('s-xl', 'Siyah', 'XL')],
  primaryImageUrl: null, totalStock: 12, startingPrice: null, currency: 'TRY', modelCode: 'M1', activePlatforms: []
} as Product

let container: HTMLDivElement
let root: Root
const errors: unknown[] = []
const onError = (event: ErrorEvent) => { errors.push(event.error); event.preventDefault() }

async function render(products: Product[] = [product]) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container, { onUncaughtError: error => { errors.push(error) } })
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter><ProductQuickEditModal mode="stock" products={products} connections={[]} onChanged={async () => undefined} onClose={() => undefined} /></MemoryRouter></QueryClientProvider>)
  })
}

const groupBox = (color: string) => document.body.querySelector<HTMLInputElement>(`input[aria-label="${color} rengindeki tüm varyantları seç"]`)!
const note = () => document.body.querySelector('.rr-note')?.textContent ?? ''
const trigger = (label: string) => Array.from(document.body.querySelectorAll<HTMLButtonElement>('button.rr-trigger')).find(item => item.textContent?.startsWith(label))!
async function click(element: HTMLElement) { await act(async () => { element.click() }) }
async function pick(label: string, option: string) {
  await click(trigger(label))
  const row = Array.from(document.body.querySelectorAll('.rr-pop label.rr-opt')).find(item => item.querySelector('.rr-grow')?.textContent === option)!
  await click(row.querySelector('input')!)
  await click(trigger(label))
}

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  errors.length = 0
  window.addEventListener('error', onError)
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  window.removeEventListener('error', onError)
  container.remove()
})

describe('ProductQuickEditModal variant selection', () => {
  // The group checkbox read event.currentTarget inside the state updater; React has cleared it by then whenever the
  // update is not computed eagerly (any click after the first), and the modal crashed.
  it('ticks and unticks a colour group on every click without an error', async () => {
    await render()
    for (let round = 0; round < 3; round++) {
      await click(groupBox('Bej'))
      expect(groupBox('Bej').checked).toBe(true)
      expect(note()).toContain('2 varyant')
      await click(groupBox('Siyah'))
      expect(note()).toContain('4 varyant')
      await click(groupBox('Bej'))
      expect(groupBox('Bej').checked).toBe(false)
      await click(groupBox('Siyah'))
      expect(note()).toContain('Varyant seçimi bekleniyor')
    }
    expect(errors).toEqual([])
  })

  it('does not open or close the group when its checkbox is clicked', async () => {
    await render()
    const header = groupBox('Bej').closest('.rr-vg-h')!.querySelector<HTMLButtonElement>('button.rr-vg-t')!
    expect(header.getAttribute('aria-expanded')).toBe('false')
    await click(groupBox('Bej'))
    expect(header.getAttribute('aria-expanded')).toBe('false')
  })

  it('starts the colour and size pickers at Seçiniz and uses them as selection shortcuts', async () => {
    await render()
    expect(trigger('Renk').textContent).toBe('Renk: Seçiniz')
    expect(trigger('Beden').textContent).toBe('Beden: Seçiniz')
    expect(note()).toContain('Varyant seçimi bekleniyor')

    await pick('Renk', 'Bej')
    expect(note()).toContain('2 varyant')
    await pick('Beden', 'XL')
    // Bej + XL: the intersection only.
    expect(note()).toContain('1 varyant')
    expect(groupBox('Bej').checked).toBe(false)

    await pick('Renk', 'Tümü')
    expect(trigger('Renk').textContent).toBe('Renk: Tümü')
    expect(note()).toContain('2 varyant')
    await pick('Beden', 'XL')
    expect(trigger('Beden').textContent).toBe('Beden: Seçiniz')
    expect(note()).toContain('4 varyant')
    expect(groupBox('Bej').checked && groupBox('Siyah').checked).toBe(true)

    // Unticking Tümü clears the selection.
    await pick('Renk', 'Tümü')
    expect(trigger('Renk').textContent).toBe('Renk: Seçiniz')
    expect(note()).toContain('Varyant seçimi bekleniyor')
    expect(errors).toEqual([])
  })

  it('clears the shortcuts once a checkbox is changed by hand', async () => {
    await render()
    await pick('Renk', 'Tümü')
    await click(groupBox('Siyah'))
    expect(trigger('Renk').textContent).toBe('Renk: Seçiniz')
    expect(note()).toContain('2 varyant')
    expect(groupBox('Bej').checked).toBe(true)
  })
})

// Live MZ028IH (6 Nov 2026): ten colour cards of one variant each. The window reads every card by id, and a card read by
// id lists the whole family's variants, so each colour showed ten rows of the same variant ("1 varyant varken 10").
describe('ProductQuickEditModal price mode switches', () => {
  async function renderPrices(phone: boolean) {
    const original = window.matchMedia
    window.matchMedia = ((query: string) => ({ ...original(query), matches: phone && query === '(max-width: 760px)' })) as typeof window.matchMedia
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    root = createRoot(container, { onUncaughtError: error => { errors.push(error) } })
    await act(async () => {
      root.render(<QueryClientProvider client={client}><MemoryRouter><ProductQuickEditModal mode="price" products={[product]} connections={[{ id: 'c1', displayName: 'Trendyol', platformCode: 'TRENDYOL' }, { id: 'c2', displayName: 'N11', platformCode: 'N11' }] as never} onChanged={async () => undefined} onClose={() => undefined} /></MemoryRouter></QueryClientProvider>)
    })
    window.matchMedia = original
  }

  it('on a phone puts each marketplace switch on its own caption line, with no separate block above the cards', async () => {
    await renderPrices(true)
    expect(document.body.querySelector('.rr-qt-customs')).toBeNull()
    // Open the colour group so its variant rows are drawn.
    for (const toggle of Array.from(document.body.querySelectorAll<HTMLButtonElement>('button.rr-vg-t'))) await click(toggle)
    const row = document.body.querySelector('table.rr-qtable tbody tr')!
    for (const name of ['Trendyol', 'N11']) {
      const cell = Array.from(row.querySelectorAll('td[data-l]')).find(item => item.getAttribute('data-l')?.startsWith(`${name} ·`))!
      const toggle = cell.querySelector(':scope > label.rr-qt-cardswitch')!
      expect(toggle.querySelector('input[role="switch"]')?.getAttribute('aria-label')).toBe(`${name} özel fiyat`)
      expect(toggle.textContent).toBe('Özel Fiyat')
    }
  })

  it('on desktop keeps the switches in the column titles only', async () => {
    await renderPrices(false)
    for (const toggle of Array.from(document.body.querySelectorAll<HTMLButtonElement>('button.rr-vg-t'))) await click(toggle)
    expect(document.body.querySelector('.rr-qt-cardswitch')).toBeNull()
    // One title switch per marketplace in each colour group's table (two groups, two marketplaces).
    expect(document.body.querySelectorAll('th .rr-qt-custom').length).toBe(4)
  })
})

describe('ProductQuickEditModal on a family of colour cards', () => {
  const colors = ['Bordo', 'Füme', 'Gri']
  const familyVariants = colors.map((color, index) => ({ ...variant(`v${index}`, color, 'Standart'), productId: `card${index}`, onHand: index + 1 }))
  // Each card as GET /products/{id} returns it: its own fields, every card's variants.
  const cards = colors.map((color, index) => ({ ...product, id: `card${index}`, title: `${color} Hırka`, variants: familyVariants }) as Product)

  it('shows each variant once, under its own colour', async () => {
    await render(cards)
    for (const color of colors) {
      const group = groupBox(color).closest('.rr-vgroup')!
      expect(group.querySelector('.rr-vg-name small')?.textContent).toBe('1 varyant')
    }
    await pick('Renk', 'Tümü')
    expect(note()).toContain('3 varyant')
    await click(groupBox('Bordo').closest('.rr-vg-h')!.querySelector<HTMLButtonElement>('button.rr-vg-t')!)
    // A stock window opens the groups of one row together (Bordo and Füme here): each shows its own variant only.
    const rowsOf = (color: string) => Array.from(document.body.querySelectorAll(`[aria-label="${color} varyantları"] tbody tr`)).map(row => row.querySelector('.rr-qname small')?.textContent)
    expect(rowsOf('Bordo')).toEqual(['SKU-v0'])
    expect(rowsOf('Füme')).toEqual(['SKU-v1'])
    expect(errors).toEqual([])
  })

  it('writes the stock of each selected variant once, by its own id', async () => {
    const calls: Array<{ path: string; body: { onHand: number; expectedOnHand: number } }> = []
    vi.mocked(hubApi).mockImplementation(async (path: string, init?: RequestInit) => {
      if (init?.method === 'PUT' && path.startsWith('/inventory/')) calls.push({ path, body: JSON.parse(String(init.body)) })
      return {} as never
    })
    await render(cards)
    await pick('Renk', 'Tümü')
    const amount = document.body.querySelector<HTMLInputElement>('input[placeholder="Miktar"]')!
    await click(document.body.querySelector<HTMLButtonElement>('button[aria-label^="Stok İşlemi"]')!)
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('[role="option"]')).find(option => option.textContent === 'Bu Kadar Ekle (+)')!)
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(amount, '5')
      amount.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => { document.body.querySelector<HTMLFormElement>('#product-quick-edit-form')!.requestSubmit() })
    expect(calls.map(call => call.path).sort()).toEqual(['/inventory/v0/on-hand', '/inventory/v1/on-hand', '/inventory/v2/on-hand'])
    expect(Object.fromEntries(calls.map(call => [call.path, call.body.onHand]))).toEqual({ '/inventory/v0/on-hand': 6, '/inventory/v1/on-hand': 7, '/inventory/v2/on-hand': 8 })
    expect(calls.every(call => call.body.onHand - call.body.expectedOnHand === 5)).toBe(true)
  })
})

describe('ProductQuickEditModal with the products list copies', () => {
  // The products list leaves out each variant's channel offers (platformStatuses); the window shows the list copies until
  // each product is read by id. The "Özel Fiyat" choice must come from the read copies, never from the list's.
  const listCopy = { ...product, variants: product.variants.map(item => ({ ...item, platformStatuses: undefined })) } as Product
  const readCopy = (followsPanelPrice: boolean) => ({ ...product, variants: product.variants.map(item => ({ ...item, platformStatuses: [{ platform: 'Trendyol', platformCode: 'TRENDYOL', isLinked: true, connectionId: 'c1', offerId: `o-${item.id}`, offerVersion: 1, listPrice: 500, salePrice: 450, currency: 'TRY', followsPanelPrice }] })) }) as Product
  const switches = () => Array.from(document.body.querySelectorAll<HTMLInputElement>('input.rr-qt-switch'))
  async function renderInTurn(...steps: Product[][]) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const view = (products: Product[]) => <QueryClientProvider client={client}><MemoryRouter><ProductQuickEditModal mode="price" products={products} connections={[{ id: 'c1', displayName: 'Trendyol', platformCode: 'TRENDYOL' }] as never} onChanged={async () => undefined} onClose={() => undefined} /></MemoryRouter></QueryClientProvider>
    root = createRoot(container, { onUncaughtError: error => { errors.push(error) } })
    for (const [index, products] of steps.entries()) {
      await act(async () => { root.render(view(products)) })
      // Open the colour groups so their tables (and the switches in their column titles) are drawn.
      if (index === 0) for (const toggle of Array.from(document.body.querySelectorAll<HTMLButtonElement>('button.rr-vg-t'))) await click(toggle)
    }
  }

  it('counts every marketplace as ticked while only the list copies are shown', async () => {
    await renderInTurn([listCopy])
    expect(switches().length).toBeGreaterThan(0)
    expect(switches().every(item => item.checked)).toBe(true)
    expect(errors).toEqual([])
  })

  it('takes the stored choice of the read copies when the list copies came first', async () => {
    await renderInTurn([listCopy], [readCopy(true)])
    // The offers follow the panel price: the switch is off, read from the offers, not fixed by the list copies.
    expect(switches().length).toBeGreaterThan(0)
    expect(switches().every(item => !item.checked)).toBe(true)
  })

  it('keeps a marketplace ticked when its read offers keep their own price', async () => {
    await renderInTurn([listCopy], [readCopy(false)])
    expect(switches().every(item => item.checked)).toBe(true)
  })
})
