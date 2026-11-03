import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return { ...original, hubApi: vi.fn(async (path: string) => { throw new Error(`Test API fixture does not handle ${path}`) }) }
})

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

async function render() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container, { onUncaughtError: error => { errors.push(error) } })
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter><ProductQuickEditModal mode="stock" products={[product]} connections={[]} onChanged={async () => undefined} onClose={() => undefined} /></MemoryRouter></QueryClientProvider>)
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
