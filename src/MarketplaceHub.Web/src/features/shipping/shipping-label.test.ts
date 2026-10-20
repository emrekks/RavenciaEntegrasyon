import { describe, expect, it } from 'vitest'
import { addressBlockFields, defaultShippingLabelSettings, normalizeShippingLabelSettings, shippingLabelPageCss } from './shipping-label'

describe('shipping label settings', () => {
  it('preserves multiple barcode blocks with distinct ids', () => {
    const trackingBarcode = defaultShippingLabelSettings.layout.sticker.find(block => block.kind === 'trackingBarcode')!
    const settings = normalizeShippingLabelSettings({
      layout: {
        a4: defaultShippingLabelSettings.layout.a4,
        sticker: [trackingBarcode, { ...trackingBarcode, id: 'trackingBarcode-2', position: { x: 8, y: 22, width: 84, height: 18 } }]
      }
    })

    expect(settings.layout.sticker.filter(block => block.kind === 'trackingBarcode')).toHaveLength(2)
    expect(settings.layout.sticker.map(block => block.id)).toEqual(['trackingBarcode', 'trackingBarcode-2'])
  })
})

describe('shipping label address block', () => {
  it('prints the recipient name before the address', () => {
    expect(addressBlockFields(['address', 'customerName'])).toEqual(['customerName', 'address'])
    expect(addressBlockFields([])).toEqual(['customerName', 'address'])
    expect(addressBlockFields(['address'])).toEqual(['address'])
  })

  it('sizes the printed page to the sticker', () => {
    expect(shippingLabelPageCss('sticker', { stickerWidthMm: 100, stickerHeightMm: 150 })).toContain('size: 100mm 150mm')
    expect(shippingLabelPageCss('a4', { stickerWidthMm: 100, stickerHeightMm: 150 })).toContain('size: A4 portrait')
  })
})
