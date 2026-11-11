import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return { ...original, hubApi: vi.fn(async (path: string) => { throw new Error(`Test API fixture does not handle ${path}`) }) }
})

import { hubApi } from '../../../shared/api'
import { ProductQuickEditModal } from './ProductQuickEditModal'
import { applyQuickEditDraft, quickEditDraftValues, type QuickEditDraftChange, type QuickEditDraftRow } from './quick-edit-draft'

const row = (key: string, color: string, size: string, values: Partial<QuickEditDraftRow> = {}): QuickEditDraftRow => ({ key, optionSignature: `Renk:${color}|Beden:${size}`, options: { Renk: color, Beden: size }, sku: `SKU-${key}`, barcode: '', stock: 3, salePrice: 100, listPrice: 120, costPrice: null, ...values })
const rows = [row('b-s', 'Bej', 'S'), row('b-xl', 'Bej', 'XL'), row('s-s', 'Siyah', 'S', { costPrice: 40 })]
const noBulk = { listPrice: '', salePrice: '', costPrice: '', stockAction: 'SET' as const, stockAmount: '' }
const cells = (list = rows) => Object.fromEntries(list.map(item => [item.key, quickEditDraftValues(item)]))

describe('applyQuickEditDraft', () => {
  it('gives the bulk values to the ticked rows only and returns just the rows that changed', () => {
    const result = applyQuickEditDraft(rows, cells(), new Set(['b-s', 'b-xl']), { listPrice: '250', salePrice: '199,90', costPrice: '80', stockAction: 'ADD', stockAmount: '2' })
    expect(result).toEqual({ ok: true, changes: [
      { key: 'b-s', stock: 5, listPrice: 250, salePrice: 199.9, costPrice: 80 },
      { key: 'b-xl', stock: 5, listPrice: 250, salePrice: 199.9, costPrice: 80 }
    ] })
  })

  it('takes a cell typed by hand, with a decimal comma, and leaves an untouched row out', () => {
    const typed = cells(); typed['s-s'] = { ...typed['s-s'], costPrice: '45,5', stock: '9' }
    expect(applyQuickEditDraft(rows, typed, new Set(), noBulk)).toEqual({ ok: true, changes: [{ key: 's-s', stock: 9, listPrice: 120, salePrice: 100, costPrice: 45.5 }] })
  })

  it('refuses a list price below the sale price, a zero price and a stock below zero, naming the variant', () => {
    expect(applyQuickEditDraft(rows, cells(), new Set(['b-s']), { ...noBulk, salePrice: '150' }, item => item.sku)).toEqual({ ok: false, message: 'SKU-b-s: liste fiyatı satış fiyatından küçük olamaz.' })
    expect(applyQuickEditDraft(rows, cells(), new Set(['b-s']), { ...noBulk, salePrice: '0' })).toMatchObject({ ok: false })
    expect(applyQuickEditDraft(rows, cells(), new Set(['b-s']), { ...noBulk, stockAction: 'SUBTRACT', stockAmount: '4' })).toMatchObject({ ok: false, message: expect.stringContaining('sıfırın altına') })
  })

  it('keeps an emptied cost unknown (null), never 0', () => {
    const typed = cells(); typed['s-s'] = { ...typed['s-s'], costPrice: '' }
    expect(applyQuickEditDraft(rows, typed, new Set(), noBulk)).toEqual({ ok: true, changes: [{ key: 's-s', stock: 3, listPrice: 120, salePrice: 100, costPrice: null }] })
  })
})

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  vi.mocked(hubApi).mockClear()
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  container.remove()
})

async function click(element: HTMLElement) { await act(async () => { element.click() }) }
async function type(input: HTMLInputElement, value: string) {
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}
const field = (label: string) => Array.from(document.body.querySelectorAll('label.rr-fld')).find(item => item.querySelector('small')?.textContent === label)!.querySelector('input')!

describe('ProductQuickEditModal opened from the product editor', () => {
  it('applies to the editor rows without calling the server, and shows Maliyet', async () => {
    const onApply = vi.fn<(changes: QuickEditDraftChange[]) => void>()
    const onClose = vi.fn()
    root = createRoot(container)
    await act(async () => { root.render(<MemoryRouter><ProductQuickEditModal mode="both" onClose={onClose} draft={{ title: 'Gömlek', modelCode: 'MZ070', rows, onApply }} /></MemoryRouter>) })

    expect(document.body.textContent).toContain('Model Kodu: MZ070')
    expect(document.body.textContent).not.toContain('Fiyat Kanalı')
    await click(document.body.querySelector<HTMLInputElement>('input[aria-label="Bej rengindeki tüm varyantları seç"]')!)
    await type(field('Satış Fiyatı'), '150')
    await type(field('Liste Fiyatı'), '180')
    await type(field('Maliyet'), '70')
    await type(field('Miktar'), '8')
    // A hand-typed cell in an open group is applied too.
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button.rr-vg-t')).find(item => item.textContent?.includes('Siyah'))!)
    expect(Array.from(document.body.querySelectorAll('th')).some(item => item.textContent === 'Maliyet')).toBe(true)
    await type(document.body.querySelector<HTMLInputElement>('input[aria-label="Siyah · S stok"]')!, '11')
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)

    expect(onApply).toHaveBeenCalledWith([
      { key: 'b-s', stock: 8, listPrice: 180, salePrice: 150, costPrice: 70 },
      { key: 'b-xl', stock: 8, listPrice: 180, salePrice: 150, costPrice: 70 },
      { key: 's-s', stock: 11, listPrice: 120, salePrice: 100, costPrice: 40 }
    ])
    expect(onClose).toHaveBeenCalled()
    expect(hubApi).not.toHaveBeenCalled()
  })

  it('applies the values to every row when no variant is ticked', async () => {
    const onApply = vi.fn()
    root = createRoot(container)
    await act(async () => { root.render(<MemoryRouter><ProductQuickEditModal mode="both" onClose={() => undefined} draft={{ title: 'Gömlek', modelCode: 'MZ070', rows, onApply }} /></MemoryRouter>) })
    expect(document.body.querySelector('.rr-note')?.textContent).toContain('Seçim yok: 3 varyantın tümüne uygulanır')
    await type(field('Maliyet'), '55')
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)
    expect(onApply).toHaveBeenCalledWith(rows.map(item => ({ key: item.key, stock: 3, listPrice: 120, salePrice: 100, costPrice: 55 })))
  })

  it('keeps the window open and applies nothing when a value breaks a rule', async () => {
    const onApply = vi.fn()
    root = createRoot(container)
    await act(async () => { root.render(<MemoryRouter><ProductQuickEditModal mode="both" onClose={() => undefined} draft={{ title: 'Gömlek', modelCode: 'MZ070', rows, onApply }} /></MemoryRouter>) })
    await click(document.body.querySelector<HTMLInputElement>('input[aria-label="Bej rengindeki tüm varyantları seç"]')!)
    await type(field('Satış Fiyatı'), '500')
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)
    expect(onApply).not.toHaveBeenCalled()
    expect(document.body.querySelector('.rr-callout')?.textContent).toContain('liste fiyatı satış fiyatından küçük olamaz')
  })
})
