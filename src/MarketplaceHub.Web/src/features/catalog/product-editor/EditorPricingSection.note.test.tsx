// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { EditorPricingSection } from './EditorPricingSection'
import type { EditorContext } from './editor-context'

vi.mock('./PriceSuggestionDialog', () => ({ PriceSuggestionLink: () => null }))
vi.mock('./SizeChartDialog', () => ({ SizeChartButton: () => null }))

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
let container: HTMLDivElement
let root: Root
beforeEach(() => { container = document.createElement('div'); document.body.appendChild(container) })
afterEach(() => { act(() => root.unmount()); container.remove() })

const card = { code: 'trendyol', name: 'Trendyol', initial: 'T', tone: 'trendyol', connection: { id: 'c1', platformCode: 'TRENDYOL', displayName: 'Trendyol' } }
function editor(rows: Array<Record<string, unknown>>, edited: boolean, salePrice = '150') {
  return {
    editProductId: 'p1', form: { listPrice: '', salePrice: '', costPrice: '', vatRate: '', vatIncluded: false, stock: '' }, variantRows: rows, setVariantRows: () => undefined,
    selectedChannelIds: ['c1'], channelPricing: { c1: { listPrice: '180', salePrice } }, fieldErrors: {}, setFieldErrors: () => undefined, updateField: () => undefined,
    requiredError: () => '', requiredFieldInvalid: () => false, variantStockTotal: 0, marketplacePricingOpen: true, setMarketplacePricingOpen: () => undefined,
    platformCards: [card], sizeChartSizes: [], sizeChartDisabledReason: '', showVariantStockTotal: false, updateVariantRow: () => undefined, applySizeChart: () => undefined,
    toggleCustomChannelPrice: () => undefined, updateChannelPrice: () => undefined, channelPriceEdited: () => edited, channelPriceDraft: () => ({ listPrice: '180', salePrice })
  } as unknown as EditorContext
}
const status = (salePrice: number) => [{ connectionId: 'c1', platformCode: 'TRENDYOL', platform: 'Trendyol', isLinked: true, salePrice }]
async function render(context: EditorContext) { root = createRoot(container); await act(async () => { root.render(<EditorPricingSection editor={context} />) }) }

describe('Pazaryerlerine Özel Fiyat note', () => {
  it('says every variant gets the card price once it was changed, and warns when the variants differ today', async () => {
    await render(editor([{ key: 'a', platformStatuses: status(100) }, { key: 'b', platformStatuses: status(120) }], true))
    const note = container.querySelector('.marketplace-pricing-note')
    expect(note?.textContent).toContain('Kaydedince Trendyol pazaryerindeki 2 varyantın fiyatı')
    expect(note?.textContent).toContain('150')
    expect(note?.textContent).toContain('farklı fiyatlar var')
    expect(container.querySelector('.marketplace-pricing-openrow .title-hint')).not.toBeNull()
  })

  it('has no difference warning when the variants share one price, and no note while the card is untouched', async () => {
    await render(editor([{ key: 'a', platformStatuses: status(100) }, { key: 'b', platformStatuses: status(100) }], true))
    expect(container.querySelector('.marketplace-pricing-note')?.textContent).not.toContain('farklı fiyatlar')
    act(() => root.unmount()); container.innerHTML = ''
    await render(editor([{ key: 'a' }, { key: 'b' }], false))
    expect(container.querySelector('.marketplace-pricing-note')).toBeNull()
  })

  it('shows neither note nor hint for a one-variant product', async () => {
    await render(editor([{ key: 'a', platformStatuses: status(100) }], true))
    expect(container.querySelector('.marketplace-pricing-note')).toBeNull()
    expect(container.querySelector('.marketplace-pricing-openrow .title-hint')).toBeNull()
  })
})
