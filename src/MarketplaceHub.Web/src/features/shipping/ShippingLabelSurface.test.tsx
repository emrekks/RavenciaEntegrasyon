import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { defaultShippingLabelSettings, normalizeShippingLabelSettings, type ShippingLabelBlock } from './shipping-label'
import { ShippingLabelBlockBody, ShippingLabelSurface, type ShippingLabelValues } from './ShippingLabelSurface'

// Sample values only: no real order's data.
const values: ShippingLabelValues = { trackingNumber: '73300036563130080', packageNumber: '4293901515', orderNumber: '11771694031', customerName: 'Örnek Alıcı', address: 'Örnek Mah. Deneme Sok. No:1\nMerkez / İzmir', cargoProvider: 'Trendyol Express', senderName: 'Ravencia', senderAddress: '', customerEmail: 'musteri@example.com' }
const sender: ShippingLabelBlock = { id: 'sender', kind: 'sender', title: 'Gönderici', fields: ['cargoProvider', 'senderName'], align: 'left', text: '' }

let container: HTMLDivElement
let root: Root
beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
afterEach(() => { act(() => root.unmount()); container.remove() })

function draw(block: ShippingLabelBlock) {
  act(() => root.render(<div className="lbl-sheet"><ShippingLabelBlockBody block={block} values={values} showCustomerPhone /></div>))
  return container.querySelector<HTMLElement>('.lbl-body')!
}
const fieldTexts = (element: Element) => Array.from(element.querySelectorAll('.lbl-field')).map(field => Array.from(field.children).map(part => `${part.className}:${part.textContent}`))

describe('shipping label block layout', () => {
  it('draws a block saved before the layout options exactly as before: caption above value, one field a row', () => {
    const body = draw(sender)
    expect(body.className).toBe('lbl-body')
    expect(body.querySelector('.lbl-row')).toBeNull()
    expect(Array.from(body.children).every(child => child.classList.contains('lbl-field'))).toBe(true)
    expect(fieldTexts(body)).toEqual([['lbl-caption:Kargo Firması', 'lbl-value:Trendyol Express'], ['lbl-caption:Gönderici Adı', 'lbl-value:Ravencia']])
  })

  it('Başlık Yanında with two fields a row puts "Kargo Firması: …" and "Gönderici Adı: …" on one row', () => {
    const body = draw({ ...sender, captionStyle: 'beside', fieldsPerRow: 2 })
    expect(body.classList.contains('is-inline')).toBe(true)
    const rows = body.querySelectorAll<HTMLElement>('.lbl-row')
    expect(rows).toHaveLength(1)
    expect(rows[0].style.getPropertyValue('--lbl-cols')).toBe('2')
    expect(fieldTexts(rows[0])).toEqual([['lbl-caption:Kargo Firması', 'lbl-value:Trendyol Express'], ['lbl-caption:Gönderici Adı', 'lbl-value:Ravencia']])
  })

  it('an older Tek Satır block still draws its captions beside the values', () => {
    expect(draw({ ...sender, inline: true }).classList.contains('is-inline')).toBe(true)
    expect(draw({ ...sender, inline: true, captionStyle: 'above' }).classList.contains('is-inline')).toBe(false)
  })

  it('Başlıksız drops the captions and the block title, and a frame and capitals are classes of the body', () => {
    const body = draw({ id: 'address', kind: 'address', title: 'Teslimat Adresi', fields: ['customerName', 'address'], align: 'left', text: '', captionStyle: 'none', boxed: true, uppercase: true })
    expect(body.querySelector('.lbl-title')).toBeNull()
    expect(body.querySelector('.lbl-caption')).toBeNull()
    expect(fieldTexts(body)).toEqual([['lbl-value:Örnek Alıcı']])
    expect(body.className).toBe('lbl-body is-boxed is-upper')
    expect(body.getAttribute('lang')).toBe('tr')
  })

  it('keeps a title and barcode out of the row grid; three a row groups the fields that follow each other', () => {
    const body = draw({ id: 'c', kind: 'custom', title: 'Bilgi', fields: ['orderNumber', 'cargoProvider', 'senderName', 'customerEmail'], align: 'left', text: '', fieldsPerRow: 3 })
    expect(Array.from(body.children).map(child => child.className)).toEqual(['lbl-title', 'lbl-row'])
    expect(body.querySelector('.lbl-row')!.children).toHaveLength(4)
  })

  it('the print draws the same layout as the designer: one renderer for both', () => {
    const settings = normalizeShippingLabelSettings({ ...defaultShippingLabelSettings, layout: { a4: defaultShippingLabelSettings.layout.a4, sticker: [{ ...sender, captionStyle: 'beside', fieldsPerRow: 2, position: { x: 3, y: 3, width: 94, height: 10 } }] } })
    act(() => root.render(<ShippingLabelSurface settings={settings} format="sticker" values={values} />))
    const row = container.querySelector('.shipping-label-print-surface .lbl-body.is-inline .lbl-row')!
    expect(fieldTexts(row)).toEqual([['lbl-caption:Kargo Firması', 'lbl-value:Trendyol Express'], ['lbl-caption:Gönderici Adı', 'lbl-value:Ravencia']])
  })
})

describe('shipping label settings keep older designs', () => {
  it('stores the layout options only when set and drops unknown values', () => {
    const [plain, set, wrong] = normalizeShippingLabelSettings({ layout: { sticker: [sender, { ...sender, id: 's2', captionStyle: 'none', fieldsPerRow: 3, boxed: true, uppercase: true }, { ...sender, id: 's3', captionStyle: 'sideways', fieldsPerRow: 5, boxed: 'yes' }] } }).layout.sticker
    expect(['captionStyle', 'fieldsPerRow', 'boxed', 'uppercase'].some(key => key in plain)).toBe(false)
    expect(set).toMatchObject({ captionStyle: 'none', fieldsPerRow: 3, boxed: true, uppercase: true })
    expect(['captionStyle', 'fieldsPerRow', 'boxed', 'uppercase'].some(key => key in wrong)).toBe(false)
  })

  it('keeps an upright block on the paper as before, and lets a turned block’s own box start off it', () => {
    const [upright, turned] = normalizeShippingLabelSettings({ layout: { sticker: [{ ...sender, position: { x: -5, y: 90, width: 60, height: 30 } }, { ...sender, id: 't', rotation: 90, position: { x: -20, y: 36, width: 60, height: 19 } }] } }).layout.sticker
    expect(upright.position).toEqual({ x: 0, y: 90, width: 60, height: 10 })
    expect(turned.position).toEqual({ x: -20, y: 36, width: 60, height: 19 })
  })

  it('the sticker default is the Trendyol Express layout on a 100 x 100 mm sticker, with a turned side barcode', () => {
    expect(defaultShippingLabelSettings.stickerWidthMm).toBe(100)
    expect(defaultShippingLabelSettings.stickerHeightMm).toBe(100)
    const sticker = defaultShippingLabelSettings.layout.sticker
    expect(sticker.map(block => block.id)).toEqual(['trackingBarcode', 'address', 'recipient', 'orderInfo', 'trackingBarcodeSide'])
    expect(sticker.find(block => block.id === 'trackingBarcodeSide')).toMatchObject({ kind: 'trackingBarcode', rotation: 90 })
    expect(sticker.filter(block => block.boxed).map(block => block.id)).toEqual(['address', 'recipient'])
    // It survives the settings' own normalizing unchanged.
    expect(normalizeShippingLabelSettings(defaultShippingLabelSettings).layout.sticker).toEqual(sticker)
  })
})
