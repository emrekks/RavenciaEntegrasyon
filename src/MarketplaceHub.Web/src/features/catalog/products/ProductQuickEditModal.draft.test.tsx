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

  // O2: a row loaded with a stored 99.999 price, a 12.3456 cost and a 2.5 stock applies untouched as it is.
  it('keeps untouched four-decimal prices and a fractional stock as stored', () => {
    const precise = [row('p', 'Bej', 'S', { listPrice: 99.999, salePrice: 12.3456, costPrice: 12.3456, stock: 2.5 })]
    expect(applyQuickEditDraft(precise, cells(precise), new Set(), noBulk)).toEqual({ ok: true, changes: [] })
    const typed = { p: { ...quickEditDraftValues(precise[0]), salePrice: '13' } }
    expect(applyQuickEditDraft(precise, typed, new Set(), noBulk)).toEqual({ ok: true, changes: [{ key: 'p', stock: 2.5, listPrice: 99.999, salePrice: 13, costPrice: 12.3456 }] })
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
async function pickValue(name: string) {
  await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
  await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).find(item => item.textContent?.startsWith(name))!.click() })
}
const field = (label: string) => Array.from(document.body.querySelectorAll('label.rr-fld')).find(item => item.querySelector('small')?.textContent === label)!.querySelector('input')!

describe('ProductQuickEditModal opened from the product editor', () => {
  it('applies to the editor rows without calling the server, and shows Maliyet', async () => {
    const onApply = vi.fn<(changes: QuickEditDraftChange[]) => void>()
    const onClose = vi.fn()
    root = createRoot(container)
    await act(async () => { root.render(<MemoryRouter><ProductQuickEditModal onClose={onClose} draft={{ title: 'Gömlek', modelCode: 'MZ070', rows, onApply }} /></MemoryRouter>) })

    expect(document.body.textContent).toContain('Model Kodu: MZ070')
    expect(document.body.textContent).not.toContain('Fiyat Kanalı')
    await click(document.body.querySelector<HTMLInputElement>('input[aria-label="Bej rengindeki tüm varyantları seç"]')!)
    // One value shows at a time (named in the band above the sizes); what was typed for the others is kept and applied together.
    expect(document.body.querySelector('.rr-cr-band')?.textContent).toBe('Stok (Adet)')
    await type(field('Miktar (Adet)'), '8')
    await pickValue('Satış Fiyatı')
    expect(document.body.querySelector('.rr-cr-band')?.textContent).toBe('Satış Fiyatı (₺)')
    await type(field('Satış Fiyatı (₺)'), '150')
    await pickValue('Liste Fiyatı')
    expect(document.body.querySelector('.rr-cr-band')?.textContent).toBe('Liste Fiyatı (₺)')
    expect(document.body.textContent).toContain('Liste Fiyatını Düzenle')
    await type(field('Liste Fiyatı (₺)'), '180')
    await pickValue('Maliyet')
    expect(document.body.querySelector('.rr-cr-band')?.textContent).toBe('Maliyet (₺)')
    await type(field('Maliyet (₺)'), '70')
    // A hand-typed cell is applied too; the tabs name the editable values.
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    expect(Array.from(document.body.querySelectorAll('[role="option"]')).map(item => item.textContent)).toEqual(['Stok', 'Maliyet', 'Liste Fiyatı', 'Satış Fiyatı'])
    await act(async () => { document.body.querySelector<HTMLElement>('[role="option"]')!.click() })
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
    await act(async () => { root.render(<MemoryRouter><ProductQuickEditModal onClose={() => undefined} draft={{ title: 'Gömlek', modelCode: 'MZ070', rows, onApply }} /></MemoryRouter>) })
    expect(document.body.querySelector('.rr-note')?.textContent).toContain('Seçim yok: 3 varyantın tümüne')
    await pickValue('Maliyet')
    await type(field('Maliyet (₺)'), '55')
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)
    expect(onApply).toHaveBeenCalledWith(rows.map(item => ({ key: item.key, stock: 3, listPrice: 120, salePrice: 100, costPrice: 55 })))
  })

  it('keeps the window open and applies nothing when a value breaks a rule', async () => {
    const onApply = vi.fn()
    root = createRoot(container)
    await act(async () => { root.render(<MemoryRouter><ProductQuickEditModal onClose={() => undefined} draft={{ title: 'Gömlek', modelCode: 'MZ070', rows, onApply }} /></MemoryRouter>) })
    await click(document.body.querySelector<HTMLInputElement>('input[aria-label="Bej rengindeki tüm varyantları seç"]')!)
    await pickValue('Satış Fiyatı')
    await type(field('Satış Fiyatı (₺)'), '500')
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)
    expect(onApply).not.toHaveBeenCalled()
    expect(document.body.querySelector('.rr-callout')?.textContent).toContain('liste fiyatı satış fiyatından küçük olamaz')
  })
})

describe('the editor window with marketplace prices', () => {
  const offers: Record<string, { list: string; sale: string }> = { 'b-s': { list: '180', sale: '150' }, 'b-xl': { list: '180', sale: '150' } }
  type Channels = NonNullable<import('./ProductQuickEditModal').QuickEditDraft['channels']>
  const channels = (custom: boolean, toggle = vi.fn()): Channels => [{ connectionId: 'c-ty', name: 'Trendyol', platformCode: 'TRENDYOL', takesListPrice: true, custom, onToggleCustom: toggle }]
  async function mount(extra: Partial<import('./ProductQuickEditModal').QuickEditDraft>, onClose: () => void = () => undefined) {
    root = createRoot(container)
    const onApplyChannelPrices = vi.fn()
    await act(async () => { root.render(<MemoryRouter><ProductQuickEditModal onClose={onClose} draft={{ title: 'Gömlek', modelCode: 'MZ070', rows, onApply: () => undefined, channelPrice: key => offers[key] ?? null, onApplyChannelPrices, ...extra }} /></MemoryRouter>) })
    return onApplyChannelPrices
  }
  async function pickChannel(name: string) {
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).find(item => item.textContent?.startsWith(name))!.click() })
  }

  it('lists a marketplace under its own heading and names it in the band', async () => {
    await mount({ channels: channels(true) })
    await pickChannel('Satış Fiyatı')
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    expect(Array.from(document.body.querySelectorAll('.pm-pop > .rr-opt-group')).map(item => item.textContent)).toContain('Trendyol')
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).filter(item => item.textContent?.startsWith('Satış Fiyatı'))[1].click() })
    expect(document.body.querySelector('.rr-cr-band')?.textContent).toBe('Trendyol · Satış Fiyatı (₺)')
  })

  it('shows a marketplace that follows the panel price read-only, with the card-level Özel Fiyat switch beside it', async () => {
    const toggle = vi.fn()
    await mount({ channels: channels(false, toggle) })
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).filter(item => item.textContent?.startsWith('Satış Fiyatı'))[1].click() })
    expect(document.body.querySelector('.rr-mx-box input[aria-label*="Trendyol"]')).toBeNull()
    expect(document.body.querySelector('.rr-mx-follow')).not.toBeNull()
    expect(document.body.textContent).toContain('Tüm varyantlar için')
    // The switch is a draft of this window (O5): the editor's switch flips only with Varyantlara Uygula.
    await click(document.body.querySelector<HTMLInputElement>('input[aria-label="Trendyol özel fiyat"]')!)
    expect(toggle).not.toHaveBeenCalled()
    expect(document.body.querySelector('.rr-mx-box input[aria-label*="Trendyol"]')).not.toBeNull()
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)
    expect(toggle).toHaveBeenCalledTimes(1)
  })

  // O5 (scan 23 Nov 2026): the window closed without a word over unapplied drafts, and Vazgeç left the switch flipped.
  it('Vazgeç with a flipped switch and a typed cell asks first, then drops them all and leaves the editor as it was', async () => {
    const toggle = vi.fn(); const onClose = vi.fn()
    const onApplyChannelPrices = await mount({ channels: channels(false, toggle) }, onClose)
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).filter(item => item.textContent?.startsWith('Satış Fiyatı'))[1].click() })
    await click(document.body.querySelector<HTMLInputElement>('input[aria-label="Trendyol özel fiyat"]')!)
    await type(document.body.querySelector<HTMLInputElement>('input[aria-label$="Trendyol satış fiyatı"]')!, '140')
    const footButton = (label: string) => Array.from(document.body.querySelectorAll<HTMLButtonElement>('footer button')).find(item => item.textContent === label)!
    await click(footButton('Vazgeç'))
    expect(onClose).not.toHaveBeenCalled()
    expect(document.body.textContent).toContain('Uygulanmamış değişiklikler var')
    await click(footButton('Düzenlemeye Dön'))
    expect(document.body.querySelector<HTMLInputElement>('input[aria-label="Trendyol özel fiyat"]')!.checked).toBe(true)
    await act(async () => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })
    expect(onClose).not.toHaveBeenCalled()
    await click(footButton('Değişiklikleri Sil ve Kapat'))
    expect(onClose).toHaveBeenCalledTimes(1)
    expect(toggle).not.toHaveBeenCalled()
    expect(onApplyChannelPrices).not.toHaveBeenCalled()
  })

  it('closes at once when nothing was typed or switched', async () => {
    const onClose = vi.fn()
    await mount({ channels: channels(true) }, onClose)
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('footer button')).find(item => item.textContent === 'Vazgeç')!)
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  // ORTA-1 (review 23 Nov): an offer stored with list 631.104 went to the editor as the text "631,104" when only the sale
  // price was typed; the editor's typed rule read it as NaN and the offer PATCH carried listPrice null. The loaded side
  // now goes over as the loaded number.
  it('hands a side left as loaded (631.104) to the editor as the loaded number', async () => {
    const onApplyChannelPrices = await mount({ channels: channels(true), channelPrice: () => ({ list: '631,104', sale: '631,104', follows: false, values: { list: 631.104, sale: 631.104 } }) })
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).filter(item => item.textContent?.startsWith('Satış Fiyatı'))[1].click() })
    await type(document.body.querySelector<HTMLInputElement>('input[aria-label$="Trendyol satış fiyatı"]')!, '600')
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)
    expect(onApplyChannelPrices).toHaveBeenCalledWith([{ rowKey: 'b-s', connectionId: 'c-ty', listPrice: 631.104, salePrice: 600 }])
  })

  it('hands a typed marketplace price to the editor as a draft and sends nothing itself', async () => {
    const onApplyChannelPrices = await mount({ channels: channels(true) })
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).filter(item => item.textContent?.startsWith('Satış Fiyatı'))[1].click() })
    await type(document.body.querySelector<HTMLInputElement>('input[aria-label$="Trendyol satış fiyatı"]')!, '140')
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)
    expect(onApplyChannelPrices).toHaveBeenCalledWith([{ rowKey: 'b-s', connectionId: 'c-ty', listPrice: 180, salePrice: 140 }])
    expect(hubApi).not.toHaveBeenCalled()
  })

  // Y2 (scan 23 Nov 2026): "1.250" showed as 1.250,00 but went to the offer as 1,25. The change set handed to the
  // editor carries the number shown, and a point used as a decimal point is refused, never guessed.
  it.each([
    ['1.250', '1250'],
    ['1.250,50', '1250,5'],
    ['1250,5', '1250,5'],
    ['12,5', '12,5'],
    ['149', '149'],
  ])('hands "%s" typed into a marketplace cell to the editor as %s', async (typed, sent) => {
    const onApplyChannelPrices = await mount({ channels: channels(true) })
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).filter(item => item.textContent?.startsWith('Liste Fiyatı'))[1].click() })
    await type(document.body.querySelector<HTMLInputElement>('input[aria-label$="Trendyol liste fiyatı"]')!, typed)
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)
    const expectedSale = Number(sent.replace(',', '.')) < 150 ? null : '150'
    if (expectedSale == null) { expect(onApplyChannelPrices).not.toHaveBeenCalled(); expect(document.body.textContent).toContain('liste fiyatı satış fiyatından küçük olamaz'); return }
    expect(onApplyChannelPrices).toHaveBeenCalledWith([{ rowKey: 'b-s', connectionId: 'c-ty', listPrice: Number(sent.replace(',', '.')), salePrice: Number(expectedSale) }])
  })

  it.each(['1.25', '1250.50', '1,250.50', '1,250', '1,000'])('refuses "%s" in a marketplace cell and hands nothing to the editor', async typed => {
    const onApplyChannelPrices = await mount({ channels: channels(true) })
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).filter(item => item.textContent?.startsWith('Satış Fiyatı'))[1].click() })
    await type(document.body.querySelector<HTMLInputElement>('input[aria-label$="Trendyol satış fiyatı"]')!, typed)
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)
    expect(onApplyChannelPrices).not.toHaveBeenCalled()
    expect(document.body.querySelector('[role="alert"]')?.textContent).toContain('nokta yalnız binlik ayırıcıdır')
  })

  // O1: a mixed marketplace of the editor (b-xl follows the panel): that variant shows the panel price read-only and a
  // marketplace price typed for all goes to the variants with their own price only.
  it('keeps a following variant of a mixed marketplace at the panel price', async () => {
    const mixedOffers: Record<string, { list: string; sale: string; follows: boolean }> = { 'b-s': { list: '180', sale: '150', follows: false }, 'b-xl': { list: '180', sale: '150', follows: true } }
    const onApplyChannelPrices = await mount({ channels: channels(true), channelPrice: key => mixedOffers[key] ?? null })
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).filter(item => item.textContent?.startsWith('Satış Fiyatı'))[1].click() })
    expect(document.body.querySelector('input[aria-label="Bej · XL Trendyol satış fiyatı"]')).toBeNull()
    expect(document.body.querySelector('.rr-mx-follow')?.textContent).toBe('100,00')
    await type(field('Satış Fiyatı (₺)'), '140')
    await click(Array.from(document.body.querySelectorAll<HTMLButtonElement>('button[type="submit"]')).find(item => item.textContent === 'Varyantlara Uygula')!)
    expect(onApplyChannelPrices).toHaveBeenCalledWith([{ rowKey: 'b-s', connectionId: 'c-ty', listPrice: 180, salePrice: 140 }])
  })

  it('switches the marketplace off for a new product, saying to save first', async () => {
    await mount({ channels: channels(true), isNewProduct: true })
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!.click() })
    const option = Array.from(document.body.querySelectorAll<HTMLButtonElement>('[role="option"]')).filter(item => item.textContent?.includes('Önce ürünü kaydedin'))
    expect(option.length).toBeGreaterThan(0)
    expect(option.every(item => item.disabled)).toBe(true)
  })
})
