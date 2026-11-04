import { act, type ComponentProps } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BulkVariantPlatformPricingModal } from './ProductEditorPages'

// Y2 of the 2 Nov 2026 review: the "Pazaryeri Fiyatları" window lost every typed price on the next render, because the
// caller's marketplace list (or the sorted copy of it) was a new array each time and reset the drafts.

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

type Props = ComponentProps<typeof BulkVariantPlatformPricingModal>
const row = { key: 'v1', optionSignature: 'Renk:Kırmızı', options: { Renk: 'Kırmızı' }, attributeValueIds: {}, sku: 'RYP-1', barcode: 'B1', stock: 3, salePrice: 90, listPrice: 100, costPrice: null, mediaRefs: [] } as Props['row']
const rows = [row]
const savedDrafts = {}
// A fresh list on every render, the marketplaces out of the panel's order, as the editor can pass them.
const platforms = () => [
  { platform: 'Shopify', platformCode: 'SHOPIFY', isLinked: true, connectionId: 'sh', listPrice: 100, salePrice: 90 },
  { platform: 'Trendyol', platformCode: 'TRENDYOL', isLinked: true, connectionId: 'ty', listPrice: 100, salePrice: 90 }
]

let container: HTMLElement
let root: Root

beforeEach(() => {
  container = document.createElement('main')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

function render(onSave = vi.fn(), title = 'Elbise') {
  act(() => {
    root.render(<BulkVariantPlatformPricingModal row={row} rows={rows} platforms={platforms()} savedDrafts={savedDrafts} productName={title} modelCode="RYP" filterGroups={[]} matchesFilterValue={() => true} saving={false} onClose={vi.fn()} onSave={onSave} />)
  })
}

function type(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  act(() => {
    setter.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

const saveButton = () => [...document.querySelectorAll<HTMLButtonElement>('footer button')].find(button => button.textContent === 'Kaydet')!

describe('BulkVariantPlatformPricingModal', () => {
  it('keeps a typed price across renders and enables Kaydet', () => {
    const onSave = vi.fn()
    render(onSave)
    const input = document.querySelector<HTMLInputElement>('input[aria-label="Renk: Kırmızı Trendyol satış fiyatı"]')!
    expect(input.value).toBe('90')
    expect(saveButton().disabled).toBe(true)
    type(input, '85')
    // The caller renders again with an equal but new marketplace list.
    render(onSave, 'Elbise Yeni')
    render(onSave, 'Elbise Yeni 2')
    expect(document.querySelector<HTMLInputElement>('input[aria-label="Renk: Kırmızı Trendyol satış fiyatı"]')!.value).toBe('85')
    expect(saveButton().disabled).toBe(false)
    act(() => saveButton().click())
    expect(onSave).toHaveBeenCalledWith({ 'v1:TRENDYOL:ty': { listPrice: '100', salePrice: '85' } })
  })

  it('shows the marketplaces in the panel order', () => {
    render()
    const heads = [...document.querySelectorAll('.variant-platform-pricing-matrix-head strong')].map(item => item.textContent)
    expect(heads[0]).toBe('TRENDYOL')
    expect(heads.at(-1)).toBe('SHOPİFY')
  })
})
