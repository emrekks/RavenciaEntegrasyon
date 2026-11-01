import { describe, expect, it } from 'vitest'
import { code128Modules, code128Symbols, defaultShippingLabelSettings, normalizeShippingLabelSettings, type ShippingLabelBlock } from './shipping-label'
import { shippingLabelBlockParts, shippingLabelLayoutIssues, shippingLabelSizeMm, shippingLabelTextSizes, stackShippingLabelLayout } from './shipping-label-layout'

const block = (patch: Partial<ShippingLabelBlock>): ShippingLabelBlock => ({ id: 'b', kind: 'address', title: 'Teslimat Adresi', fields: ['customerName', 'address'], align: 'left', text: '', ...patch })

describe('label designer preview follows the print rules', () => {
  it('shows the address block as title, recipient name, then the address lines', () => {
    expect(shippingLabelBlockParts(block({ fields: ['address', 'customerName'] }), { showCustomerPhone: true })).toEqual([
      { type: 'title', text: 'Teslimat Adresi' },
      { type: 'field', field: 'customerName' },
      { type: 'lines', field: 'address' }
    ])
  })

  it('hides the customer contact unless the settings allow it', () => {
    const info = block({ kind: 'orderInfo', fields: ['orderNumber', 'customerEmail'] })
    expect(shippingLabelBlockParts(info, { showCustomerPhone: false })).toEqual([{ type: 'field', field: 'orderNumber' }])
    expect(shippingLabelBlockParts(info, { showCustomerPhone: true })).toHaveLength(2)
  })

  it('draws a barcode block’s barcode first and then its other fields', () => {
    expect(shippingLabelBlockParts(block({ kind: 'trackingBarcode', fields: ['trackingNumber', 'cargoProvider'] }), { showCustomerPhone: false })).toEqual([
      { type: 'barcode', field: 'trackingNumber' },
      { type: 'field', field: 'cargoProvider' }
    ])
  })

  it('prints the sender address as lines and a custom block with its title and text', () => {
    expect(shippingLabelBlockParts(block({ kind: 'sender', fields: ['senderName', 'senderAddress'], text: 'İade adresi' }), { showCustomerPhone: false })).toEqual([
      { type: 'text', text: 'İade adresi' },
      { type: 'field', field: 'senderName' },
      { type: 'lines', field: 'senderAddress' }
    ])
    expect(shippingLabelBlockParts(block({ kind: 'custom', title: 'Not', fields: [], text: 'Kırılabilir' }), { showCustomerPhone: false })).toEqual([
      { type: 'title', text: 'Not' },
      { type: 'text', text: 'Kırılabilir' }
    ])
  })
})

describe('label size, text sizes and layout checks', () => {
  it('sizes a sticker by the settings and an A4 label by its share of the page', () => {
    const settings = { stickerWidthMm: 100, stickerHeightMm: 100, a4LabelsPerPage: 1 as const }
    expect(shippingLabelSizeMm('sticker', settings)).toEqual({ width: 100, height: 100 })
    expect(shippingLabelSizeMm('a4', settings)).toEqual({ width: 194, height: 280 })
    expect(shippingLabelSizeMm('a4', { ...settings, a4LabelsPerPage: 4 })).toEqual({ width: 96.5, height: 140 })
  })

  it('moves a saved free text size to the nearest of the three steps', () => {
    const sizes = [8, 12, 13, 14, 15, 16, 24, 72].map(fontSize => normalizeShippingLabelSettings({ layout: { sticker: [{ ...defaultShippingLabelSettings.layout.sticker[0], fontSize }] } }).layout.sticker[0].fontSize)
    expect(sizes).toEqual([12, 12, 14, 14, 14, 17, 17, 17])
    expect(shippingLabelTextSizes.map(size => size.px)).toEqual([12, 14, 17])
  })

  it('warns about blocks on top of each other but not about blocks that only touch', () => {
    const a = block({ id: 'a', position: { x: 0, y: 0, width: 50, height: 20 } })
    expect(shippingLabelLayoutIssues([a, block({ id: 'b', position: { x: 0, y: 20, width: 50, height: 20 } })], { width: 100, height: 100 }, {})).toEqual([])
    expect(shippingLabelLayoutIssues([a, block({ id: 'c', position: { x: 40, y: 10, width: 50, height: 20 } })], { width: 100, height: 100 }, {})).toEqual([{ kind: 'overlap', blockIds: ['a', 'c'] }])
  })

  it('warns when a barcode is too narrow to scan, as on the live 100 × 100 sticker’s 40% block', () => {
    const barcode = block({ id: 'bc', kind: 'trackingBarcode', fields: ['trackingNumber'], position: { x: 27.5, y: 2.5, width: 40, height: 12.5 } })
    const narrow = shippingLabelLayoutIssues([barcode], { width: 100, height: 100 }, { trackingNumber: '73300385314147370' })
    expect(narrow).toEqual([expect.objectContaining({ kind: 'narrowBarcode', blockId: 'bc' })])
    expect(shippingLabelLayoutIssues([{ ...barcode, position: { ...barcode.position!, x: 8, width: 84 } }], { width: 100, height: 100 }, { trackingNumber: '73300385314147370' })).toEqual([])
  })

  it('stacks rows of blocks from the first row’s top with the gap, fitting text blocks to their content', () => {
    const layout = [
      block({ id: 'barcode', kind: 'trackingBarcode', fields: ['trackingNumber'], position: { x: 8, y: 2, width: 84, height: 15 } }),
      block({ id: 'address', position: { x: 8, y: 30, width: 40, height: 30 } }),
      block({ id: 'side', kind: 'orderInfo', fields: ['orderNumber'], position: { x: 52, y: 32, width: 40, height: 10 } }),
      block({ id: 'sender', kind: 'sender', fields: ['senderName'], position: { x: 8, y: 80, width: 84, height: 15 } })
    ]
    const stacked = stackShippingLabelLayout(layout, { contentHeights: { address: 18, side: 8, sender: 6.2, barcode: 3 }, gapPercent: 2 })!
    const at = (id: string) => stacked.find(item => item.id === id)!.position!
    expect(at('barcode')).toEqual({ x: 8, y: 2, width: 84, height: 15 })
    expect(at('address')).toEqual({ x: 8, y: 19, width: 40, height: 18 })
    expect(at('side')).toEqual({ x: 52, y: 21, width: 40, height: 8 })
    expect(at('sender')).toEqual({ x: 8, y: 39, width: 84, height: 6.5 })
    expect(stackShippingLabelLayout(layout, { contentHeights: { address: 90 }, gapPercent: 2 })).toBeNull()
  })
})

describe('Code 128 symbols', () => {
  it('packs an all-digit value two digits a symbol and ends an odd one in code set B', () => {
    expect(code128Symbols('1234')).toEqual([105, 12, 34, (105 + 12 + 34 * 2) % 103, 106])
    const odd = code128Symbols('12345')
    expect(odd.slice(0, 5)).toEqual([105, 12, 34, 100, '5'.charCodeAt(0) - 32])
    expect(odd[5]).toBe((105 + 12 + 34 * 2 + 100 * 3 + 21 * 4) % 103)
  })

  it('keeps letters in code set B and draws 11 modules a symbol plus a 13-module stop', () => {
    expect(code128Symbols('AB1')).toEqual([104, 33, 34, 17, (104 + 33 + 68 + 51) % 103, 106])
    expect(code128Modules('73300385314147370')).toHaveLength(12 * 11 + 13)
  })
})
