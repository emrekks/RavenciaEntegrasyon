import { describe, expect, it } from 'vitest'
import { defaultShippingLabelSettings, normalizeShippingLabelSettings } from './shipping-label'

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
