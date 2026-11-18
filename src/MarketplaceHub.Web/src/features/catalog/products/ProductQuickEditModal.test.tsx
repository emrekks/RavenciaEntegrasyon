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
import type { Product, Variant } from '../shared'
import { testProduct, testVariant, testVariantPlatformStatus } from '../../../test/product'

function variant(id: string, color: string, size: string): Variant {
  return testVariant({ id, sku: `SKU-${id}`, modelCode: 'M1', optionSignature: `Renk:${color}|Beden:${size}`, onHand: 3, available: 3, inventoryVersion: 1, currency: 'TRY', options: { Renk: color, Beden: size } })
}

const product: Product = testProduct({
  id: 'p1', title: 'Gömlek', updatedAt: '2026-11-01T00:00:00Z',
  variants: [variant('b-s', 'Bej', 'S'), variant('b-xl', 'Bej', 'XL'), variant('s-s', 'Siyah', 'S'), variant('s-xl', 'Siyah', 'XL')],
  totalStock: 12, modelCode: 'M1', activePlatforms: []
})

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
async function click(element: HTMLElement) { await act(async () => { element.click() }) }
const valueTrigger = () => document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!
const valueOptions = () => Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]'))
// Opens the "Düzenlenen Değer" list and picks a row: 'Stok', or a marketplace group and the row under it ('Satış' / 'Liste' / none for a single row).
async function pickValue(group: string, row?: string) {
  await act(async () => { valueTrigger().click() })
  const options = valueOptions()
  const at = options.findIndex(option => option.textContent?.startsWith(group))
  const target = row === undefined ? options[at + (options[at]?.getAttribute('aria-disabled') === 'true' || options[at]?.hasAttribute('disabled') ? 1 : 0)] : options.slice(at + 1).find(option => option.textContent?.startsWith(row))
  await act(async () => { target!.click() })
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
      expect(note()).toContain('Seçim yok: 4 varyantın tümüne uygulanır')
    }
    expect(errors).toEqual([])
  })

  it('ticks a size in every colour, a single variant and all of them; a half-ticked row shows the mixed state', async () => {
    await render()
    const columnBox = (size: string) => document.body.querySelector<HTMLButtonElement>(`button[aria-label="${size} bedenindeki tüm varyantları seç"]`)!
    const cellBox = (sku: string) => document.body.querySelector<HTMLInputElement>(`input[aria-label="${sku} varyantını seç"]`)!
    const corner = document.body.querySelector<HTMLInputElement>('input[aria-label="Tüm varyantları seç"]')!
    await click(columnBox('XL'))
    expect(note()).toContain('2 varyant seçildi')
    expect(groupBox('Bej').indeterminate).toBe(true)
    expect(groupBox('Bej').checked).toBe(false)
    await click(cellBox('Bej · S'))
    expect(groupBox('Bej').checked).toBe(true)
    expect(note()).toContain('3 varyant seçildi')
    await click(corner)
    expect(note()).toContain('4 varyant seçildi')
    expect(corner.checked).toBe(true)
    await click(corner)
    expect(note()).toContain('Seçim yok: 4 varyantın tümüne uygulanır')
    expect(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).map(button => button.textContent)).toEqual(['Tümüne Uygula'])
    expect(errors).toEqual([])
  })

  it('draws one line per colour with its sizes side by side in size order, a box beside each size label', async () => {
    await render()
    expect(Array.from(document.body.querySelectorAll('.rr-cr-row .rr-cr-color b')).map(item => item.textContent)).toEqual(['Bej', 'Siyah'])
    const sizesOf = (color: string) => Array.from(groupBox(color).closest('.rr-cr-row')!.querySelectorAll('.rr-cr-size span')).map(item => item.textContent)
    expect(sizesOf('Bej')).toEqual(['S', 'XL'])
    expect(sizesOf('Siyah')).toEqual(['S', 'XL'])
    expect(document.body.querySelectorAll('.rr-cr-row input.rr-mx-in')).toHaveLength(4)
    // No table, no opening groups, no shared header row far from the boxes.
    expect(document.body.querySelector('table.rr-mx, th.rr-mx-size, .rr-vgroup, .rr-vg-t, .rr-qtable')).toBeNull()
    expect(groupBox('Bej').classList.contains('rr-cr-round')).toBe(true)
  })

  it('draws no cell at all for a size a colour does not have', async () => {
    const missing: Product = { ...product, variants: product.variants.filter(item => item.id !== 's-xl') }
    await render([missing])
    expect(groupBox('Siyah').closest('.rr-cr-row')!.querySelectorAll('.rr-cr-pair')).toHaveLength(1)
    expect(groupBox('Bej').closest('.rr-cr-row')!.querySelectorAll('.rr-cr-pair')).toHaveLength(2)
    expect(document.body.querySelector('.rr-mx-none')).toBeNull()
    expect(document.body.querySelectorAll('.rr-cr-row input.rr-mx-in')).toHaveLength(3)
  })

  it('a size label ticks its variant and turns the pair purple', async () => {
    await render()
    const pair = document.body.querySelector('.rr-cr-pair')!
    expect(pair.classList.contains('rr-on')).toBe(false)
    await click(pair.querySelector<HTMLInputElement>('input.rr-cr-tick')!)
    expect(pair.classList.contains('rr-on')).toBe(true)
    expect(note()).toContain('1 varyant seçildi')
  })

  it('moves between the boxes with the arrow keys and Enter, skipping dashes', async () => {
    await render()
    const box = (sku: string) => document.body.querySelector<HTMLInputElement>(`input[aria-label="${sku} stok"]`)!
    const press = async (element: HTMLElement, key: string) => { await act(async () => { element.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true })) }) }
    box('SKU-b-s').focus()
    await press(box('SKU-b-s'), 'ArrowRight')
    expect(document.activeElement).toBe(box('SKU-b-xl'))
    await press(box('SKU-b-xl'), 'ArrowDown')
    expect(document.activeElement).toBe(box('SKU-s-xl'))
    await press(box('SKU-s-xl'), 'ArrowLeft')
    expect(document.activeElement).toBe(box('SKU-s-s'))
    await press(box('SKU-s-s'), 'ArrowUp')
    expect(document.activeElement).toBe(box('SKU-b-s'))
    await press(box('SKU-b-s'), 'Enter')
    expect(document.activeElement).toBe(box('SKU-s-s'))
  })
})

// Live MZ028IH (6 Nov 2026): ten colour cards of one variant each. The window reads every card by id, and a card read by
// id lists the whole family's variants, so each colour showed ten rows of the same variant ("1 varyant varken 10").
describe('ProductQuickEditModal price mode tabs', () => {
  async function renderPrices() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    root = createRoot(container, { onUncaughtError: error => { errors.push(error) } })
    await act(async () => {
      root.render(<QueryClientProvider client={client}><MemoryRouter><ProductQuickEditModal mode="price" products={[product]} connections={[{ id: 'c1', displayName: 'Trendyol', platformCode: 'TRENDYOL' }, { id: 'c2', displayName: 'N11', platformCode: 'N11' }] as never} onChanged={async () => undefined} onClose={() => undefined} /></MemoryRouter></QueryClientProvider>)
    })
  }

  it('offers every value in one list grouped by marketplace, the window starting on the panel sale price', async () => {
    await renderPrices()
    expect(valueTrigger().textContent).toContain('Panel Satış Fiyatı')
    expect(document.body.querySelector('[role="tab"], .rr-mx-tablist')).toBeNull()
    await act(async () => { valueTrigger().click() })
    const texts = valueOptions().map(option => option.textContent)
    expect(texts[0]).toBe('Stok')
    expect(texts.filter(text => text === 'Panel' || text === 'Trendyol' || text === 'N11')).toEqual(['Panel', 'Trendyol', 'N11'])
    expect(texts).toContain('Liste Fiyatı')
    // One box per variant, whatever the value.
    expect(document.body.querySelectorAll('.rr-cr-row input.rr-mx-in')).toHaveLength(4)
  })

  it('puts the Özel Fiyat switch beside a marketplace value only', async () => {
    await renderPrices()
    expect(document.body.querySelector('.rr-mx-tabs input[role="switch"]')).toBeNull()
    await pickValue('Trendyol', 'Satış')
    expect(document.body.querySelector('.rr-mx-tabs input[role="switch"]')?.getAttribute('aria-label')).toBe('Trendyol özel fiyat')
    await pickValue('N11', 'Satış')
    expect(document.body.querySelector('.rr-mx-tabs input[role="switch"]')?.getAttribute('aria-label')).toBe('N11 özel fiyat')
  })

  it('the stock window opens on Stok and offers the prices too', async () => {
    await render()
    expect(valueTrigger().textContent).toContain('Stok')
    await act(async () => { valueTrigger().click() })
    expect(valueOptions().some(option => option.textContent === 'Panel')).toBe(true)
  })
})

describe('ProductQuickEditModal on a family of colour cards', () => {
  const colors = ['Bordo', 'Füme', 'Gri']
  const familyVariants = colors.map((color, index) => ({ ...variant(`v${index}`, color, 'Standart'), productId: `card${index}`, onHand: index + 1 }))
  // Each card as GET /products/{id} returns it: its own fields, every card's variants.
  const cards = colors.map((color, index) => ({ ...product, id: `card${index}`, title: `${color} Hırka`, variants: familyVariants }) as Product)

  it('shows each variant once, under its own colour', async () => {
    await render(cards)
    for (const color of colors) expect(groupBox(color).closest('.rr-cr-row')!.querySelectorAll('input.rr-mx-in')).toHaveLength(1)
    expect(note()).toContain('3 varyant')
    // One row per colour, each with its own variant in the one size column.
    const rowsOf = (color: string) => Array.from(document.body.querySelectorAll('.rr-cr-row')).filter(row => row.querySelector('.rr-cr-color b')?.textContent === color).flatMap(row => Array.from(row.querySelectorAll('input.rr-mx-in')).map(input => input.getAttribute('aria-label')))
    expect(rowsOf('Bordo')).toEqual(['SKU-v0 stok'])
    expect(rowsOf('Füme')).toEqual(['SKU-v1 stok'])
    expect(rowsOf('Gri')).toEqual(['SKU-v2 stok'])
    expect(errors).toEqual([])
  })

  it('Tümüne Uygula only fills drafts; Kaydet then writes the stock of each variant once, by its own id', async () => {
    const calls: Array<{ path: string; body: { onHand: number; expectedOnHand: number } }> = []
    vi.mocked(hubApi).mockImplementation(async (path: string, init?: RequestInit) => {
      if (init?.method === 'PUT' && path.startsWith('/inventory/')) calls.push({ path, body: JSON.parse(String(init.body)) })
      return {} as never
    })
    // Nothing ticked: the stock goes to every variant of the window.
    await render(cards)
    const amount = document.body.querySelector<HTMLInputElement>('input[placeholder="Miktar"]')!
    await click(document.body.querySelector<HTMLButtonElement>('button[aria-label^="Stok İşlemi"]')!)
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('[role="option"]')).find(option => option.textContent === 'Bu Kadar Ekle (+)')!)
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(amount, '5')
      amount.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => { document.body.querySelector<HTMLFormElement>('#product-quick-edit-form')!.requestSubmit() })
    expect(calls).toEqual([])
    const bar = () => document.body.querySelector('.rv-draftbar')!
    expect(bar().textContent).toContain('3 Değişiklik')
    await click(Array.from(bar().querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === 'Kaydet')!)
    expect(calls).toEqual([])
    await click(Array.from(bar().querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === 'Onayla ve Kaydet')!)
    expect(calls.map(call => call.path).sort()).toEqual(['/inventory/v0/on-hand', '/inventory/v1/on-hand', '/inventory/v2/on-hand'])
    expect(Object.fromEntries(calls.map(call => [call.path, call.body.onHand]))).toEqual({ '/inventory/v0/on-hand': 6, '/inventory/v1/on-hand': 7, '/inventory/v2/on-hand': 8 })
    expect(calls.every(call => call.body.onHand - call.body.expectedOnHand === 5)).toBe(true)
  })
})

describe('ProductQuickEditModal with the products list copies', () => {
  // The products list leaves out each variant's channel offers (platformStatuses); the window shows the list copies until
  // each product is read by id. The "Özel Fiyat" choice must come from the read copies, never from the list's.
  // (null here, absent on the wire: the window treats both as not read)
  const listCopy: Product = { ...product, variants: product.variants.map(item => ({ ...item, platformStatuses: null })) }
  const readCopy = (followsPanelPrice: boolean): Product => ({ ...product, variants: product.variants.map(item => ({ ...item, platformStatuses: [testVariantPlatformStatus({ platform: 'Trendyol', platformCode: 'TRENDYOL', connectionId: 'c1', offerId: `o-${item.id}`, offerVersion: 1, listPrice: 500, salePrice: 450, currency: 'TRY', followsPanelPrice })] })) })
  const switches = () => Array.from(document.body.querySelectorAll<HTMLInputElement>('input.rr-qt-switch'))
  async function renderInTurn(...steps: Product[][]) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const view = (products: Product[]) => <QueryClientProvider client={client}><MemoryRouter><ProductQuickEditModal mode="price" products={products} connections={[{ id: 'c1', displayName: 'Trendyol', platformCode: 'TRENDYOL' }] as never} onChanged={async () => undefined} onClose={() => undefined} /></MemoryRouter></QueryClientProvider>
    root = createRoot(container, { onUncaughtError: error => { errors.push(error) } })
    for (const [index, products] of steps.entries()) {
      await act(async () => { root.render(view(products)) })
      // The switch sits beside a marketplace value: choose it.
      if (index === 0) await pickValue('Trendyol', 'Satış')
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
