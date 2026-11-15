import { afterEach, describe, expect, it } from 'vitest'
import { addressBlockFields, defaultShippingLabelSettings, loadPrintedShippingLabels, markShippingLabelPrinted, normalizeShippingLabelSettings, shippingLabelPageCss } from './shipping-label'

describe('printed shipping labels (WEB-ALTYAPI-5)', () => {
  afterEach(() => localStorage.clear())

  it('keeps the printed keys as a bounded recent list and still reads an old plain list', () => {
    localStorage.setItem('ravencia.printedShippingLabels', JSON.stringify(['old:a4']))
    expect(loadPrintedShippingLabels()).toEqual(['old:a4'])

    expect(markShippingLabelPrinted('o1', 'sticker')).toBe('o1:sticker')
    expect(markShippingLabelPrinted('o1', 'sticker')).toBe('o1:sticker')
    expect(loadPrintedShippingLabels()).toEqual(['old:a4', 'o1:sticker'])
    const stored = JSON.parse(localStorage.getItem('ravencia.printedShippingLabels') ?? '[]') as Array<{ key: string; at: number }>
    expect(stored.map(entry => entry.key)).toEqual(['old:a4', 'o1:sticker'])
    expect(stored.every(entry => Number.isFinite(entry.at))).toBe(true)
  })
})

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
