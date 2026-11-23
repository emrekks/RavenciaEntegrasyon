import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return { ...original, hubApi: vi.fn(async (path: string) => { throw new Error(`Test API fixture does not handle ${path}`) }) }
})

import { hubApi } from '../../../shared/api'
import { ProductQuickEditModal } from './ProductQuickEditModal'
import type { Product } from '../shared'
import { testProduct, testVariant, testVariantPlatformStatus } from '../../../test/product'

// Typing in the quick edit table makes drafts only (owner, 18 Nov 2026): no request goes out until Kaydet is pressed and
// confirmed; Tümüne Uygula fills drafts too; Vazgeç and Esc put the saved values back; closing asks first.
const product: Product = testProduct({
  id: 'p1', title: 'Gömlek', updatedAt: '2026-11-01T00:00:00Z', modelCode: 'M1', activePlatforms: [], totalStock: 6,
  variants: [
    testVariant({ id: 'v1', sku: 'SKU-1', modelCode: 'M1', optionSignature: 'Renk:Bej|Beden:S', onHand: 3, available: 3, inventoryVersion: 1, currency: 'TRY', platformStatuses: [], defaultListPrice: 500, defaultSalePrice: 450, version: 1, options: { Renk: 'Bej', Beden: 'S' } }),
    testVariant({ id: 'v2', sku: 'SKU-2', modelCode: 'M1', optionSignature: 'Renk:Bej|Beden:XL', onHand: 3, available: 3, inventoryVersion: 1, currency: 'TRY', platformStatuses: [], defaultListPrice: 500, defaultSalePrice: 450, version: 1, options: { Renk: 'Bej', Beden: 'XL' } })
  ]
})

let container: HTMLDivElement
let root: Root
let closed = 0
const sent: Array<{ path: string; method?: string; body: { onHand?: number; expectedOnHand?: number } }> = []

async function render(mode: 'stock' | 'price' = 'stock', connections: unknown[] = [], products: Product[] = [product]) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  root = createRoot(container)
  await act(async () => {
    root.render(<QueryClientProvider client={client}><MemoryRouter><ProductQuickEditModal mode={mode} products={products} connections={connections as never} onChanged={async () => undefined} onClose={() => { closed += 1 }} /></MemoryRouter></QueryClientProvider>)
  })
}

async function click(element: HTMLElement) { await act(async () => { element.click() }) }
const valueTrigger = () => document.body.querySelector<HTMLButtonElement>('button[aria-label^="Düzenlenen Değer"]')!
// Opens the "Düzenlenen Değer" list and picks a row: 'Stok', or a marketplace group and the row under it ('Satış' / 'Liste' / none for a single row).
async function pickValue(group: string, row?: string) {
  await act(async () => { valueTrigger().click() })
  const children = Array.from(document.body.querySelectorAll<HTMLElement>('.pm-pop > *'))
  const at = children.findIndex(item => item.textContent?.startsWith(group))
  // Stok is an option of its own; a marketplace is a heading, its rows follow it.
  const target = children[at].getAttribute('role') === 'option' ? children[at] : row === undefined ? children[at + 1] : children.slice(at + 1).find(item => item.getAttribute('role') === 'option' && item.textContent?.startsWith(row))
  await act(async () => { target!.click() })
}

async function type(input: HTMLInputElement, value: string) {
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}
const stockBox = (sku: string) => document.body.querySelector<HTMLInputElement>(`input[aria-label="${sku} stok"]`)!
const bar = () => document.body.querySelector('.rr-m-foot')
const barButton = (label: string) => Array.from(bar()!.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === label)!
const bulkField = (label: string) => Array.from(document.body.querySelectorAll('label.rr-fld')).find(item => item.querySelector('small')?.textContent === label)!.querySelector('input')!
const applyButton = () => document.body.querySelector<HTMLButtonElement>('button.rr-qe-apply')!
const footButton = (label: string) => Array.from(document.body.querySelectorAll<HTMLButtonElement>('footer button')).find(button => button.textContent === label)!

beforeEach(() => {
  ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  closed = 0
  sent.length = 0
  vi.mocked(hubApi).mockImplementation(async (path: string, init?: RequestInit) => {
    sent.push({ path, method: init?.method, body: init?.body ? JSON.parse(String(init.body)) : {} })
    return {} as never
  })
  container = document.createElement('div')
  document.body.append(container)
})

afterEach(() => {
  act(() => root?.unmount())
  container.remove()
})

describe('ProductQuickEditModal drafts', () => {
  it('writing in a stock box and leaving it sends nothing and shows an unsaved bar', async () => {
    await render()
    expect(bar()?.textContent).toContain('Değişiklik Yok')
    expect(document.body.querySelectorAll('button.rv-draftbar-save')).toHaveLength(1)
    expect(bar()?.querySelector<HTMLButtonElement>('button.rv-draftbar-save')?.disabled).toBe(true)
    const box = stockBox('SKU-1')
    await act(async () => { box.focus() })
    await type(box, '9')
    await act(async () => { box.blur() })
    await act(async () => { box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })) })
    expect(sent).toEqual([])
    expect(box.value).toBe('9')
    expect(bar()?.textContent).toContain('1 Değişiklik')
    expect(box.closest('.rr-draft')).not.toBeNull()
    expect(box.title).toContain('kayıtlı stok: 3')
  })

  it('Kaydet asks once, then writes one request carrying the stock it replaces', async () => {
    await render()
    await type(stockBox('SKU-1'), '9')
    await click(barButton('Kaydet'))
    expect(sent).toEqual([])
    expect(bar()?.textContent).toContain('1 varyantın stoğu değişecek')
    expect(bar()?.textContent).toContain('Otomatik dış yazması açık pazaryerlerine gönderilir')
    await click(barButton('Onayla ve Kaydet'))
    expect(sent.map(call => [call.path, call.method, call.body.onHand, call.body.expectedOnHand])).toEqual([['/inventory/v1/on-hand', 'PUT', 9, 3]])
    expect(bar()?.textContent).toContain('Değişiklik Yok')
  })

  it('Vazgeç puts the saved value back and sends nothing', async () => {
    await render()
    await type(stockBox('SKU-1'), '9')
    await type(stockBox('SKU-2'), '4')
    expect(bar()?.textContent).toContain('2 Değişiklik')
    await click(barButton('Vazgeç'))
    expect(sent).toEqual([])
    expect(stockBox('SKU-1').value).toBe('3')
    expect(stockBox('SKU-2').value).toBe('3')
    expect(bar()?.textContent).toContain('Değişiklik Yok')
  })

  it('Esc in one box takes back that box only', async () => {
    await render()
    await type(stockBox('SKU-1'), '9')
    await type(stockBox('SKU-2'), '4')
    await act(async () => { stockBox('SKU-1').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })
    expect(stockBox('SKU-1').value).toBe('3')
    expect(stockBox('SKU-2').value).toBe('4')
    expect(bar()?.textContent).toContain('1 Değişiklik')
    expect(closed).toBe(0)
  })

  it('a draft that fails its check keeps Kaydet off and says why', async () => {
    await render()
    await type(stockBox('SKU-1'), '-2')
    expect(barButton('Kaydet').disabled).toBe(true)
    expect(bar()?.textContent).toContain('sıfır veya pozitif bir tam sayı')
    expect(stockBox('SKU-1').getAttribute('aria-invalid')).toBe('true')
    await click(barButton('Kaydet'))
    expect(sent).toEqual([])
  })

  it('a refused (changed meanwhile) stock stays a draft with its reason', async () => {
    const { ApiRequestError } = await import('../../../shared/api')
    vi.mocked(hubApi).mockImplementation(async (path: string, init?: RequestInit) => {
      sent.push({ path, method: init?.method, body: {} })
      throw new ApiRequestError('Stok başka bir işlemle değişti.', 409, 'STOCK_CHANGED')
    })
    await render()
    await type(stockBox('SKU-1'), '9')
    await click(barButton('Kaydet'))
    await click(barButton('Onayla ve Kaydet'))
    expect(sent).toHaveLength(1)
    expect(stockBox('SKU-1').value).toBe('9')
    expect(stockBox('SKU-1').title).toContain('Stok başka bir işlemle değişti')
    expect(bar()?.textContent).toContain('1 Değişiklik')
  })

  it('closing with a draft asks first; staying keeps it, leaving drops it', async () => {
    await render()
    await type(stockBox('SKU-1'), '9')
    await act(async () => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })
    expect(closed).toBe(0)
    expect(bar()?.textContent).toContain('Kaydedilmemiş değişiklikler var')
    await click(barButton('Düzenlemeye Dön'))
    expect(bar()?.textContent).toContain('1 Değişiklik')
    await act(async () => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })) })
    await click(barButton('Değişiklikleri Sil ve Kapat'))
    expect(closed).toBe(1)
    expect(sent).toEqual([])
  })

  it('a panel price typed in a cell is a draft too: nothing goes out until Kaydet is confirmed', async () => {
    await render('price')
    const sale = document.body.querySelector<HTMLInputElement>('input[aria-label="SKU-1 Panel Ana Fiyatı satış fiyatı"], input[aria-label^="SKU-1"][aria-label$="satış fiyatı"]')!
    await act(async () => { sale.focus() })
    await type(sale, '460')
    await act(async () => { sale.blur() })
    expect(sent).toEqual([])
    expect(bar()?.textContent).toContain('1 Değişiklik')
    await click(barButton('Kaydet'))
    expect(sent).toEqual([])
    expect(bar()?.textContent).toContain('1 fiyat hücresi değişecek')
    await click(barButton('Onayla ve Kaydet'))
    expect(sent.some(call => call.path.endsWith('/default-prices') && call.method === 'PATCH')).toBe(true)
  })

  it('the Özel Fiyat switch is a draft too: saved only with Kaydet, and named in the confirmation', async () => {
    const withOffers: Product = { ...product, variants: product.variants.map(item => ({ ...item, platformStatuses: [testVariantPlatformStatus({ platform: 'Trendyol', platformCode: 'TRENDYOL', connectionId: 'c1', offerId: `o-${item.id}`, offerVersion: 1, listPrice: 500, salePrice: 450, currency: 'TRY', followsPanelPrice: false })] })) }
    await render('price', [{ id: 'c1', displayName: 'Trendyol', platformCode: 'TRENDYOL' }], [withOffers])
    await pickValue('Trendyol', 'Satış')
    const toggle = document.body.querySelector<HTMLInputElement>('input.rr-qt-switch')!
    expect(toggle.checked).toBe(true)
    await click(toggle)
    expect(toggle.checked).toBe(false)
    expect(sent).toEqual([])
    expect(bar()?.textContent).toContain('1 Değişiklik')
    await click(barButton('Kaydet'))
    expect(sent).toEqual([])
    expect(bar()?.textContent).toContain('1 kanalda Özel Fiyat seçimi değişecek')
    await click(barButton('Onayla ve Kaydet'))
    expect(sent.map(call => [call.path, call.method])).toEqual([['/channel-offers/panel-price-follow', 'PUT']])
    expect(toggle.checked).toBe(false)
    expect(bar()?.textContent).toContain('Değişiklik Yok')
  })

  it('keeps a draft when the edited value changes, marking the dropdown that holds it', async () => {
    await render()
    await type(stockBox('SKU-1'), '9')
    expect(valueTrigger().querySelector('.rr-mx-dot')).not.toBeNull()
    await pickValue('Panel', 'Satış')
    expect(document.body.querySelector('input[aria-label$="stok"]')).toBeNull()
    expect(bar()?.textContent).toContain('1 Değişiklik')
    await pickValue('Stok')
    expect(stockBox('SKU-1').value).toBe('9')
    expect(sent).toEqual([])
  })

  it('Uygula sits at the end of the value row, is off while the boxes are empty and only writes drafts; there is one Kaydet', async () => {
    await render()
    const apply = document.body.querySelector<HTMLButtonElement>('button.rr-qe-apply')!
    expect(apply.textContent).toBe('Uygula')
    expect(apply.closest('.rr-qe-applyrow')).not.toBeNull()
    expect(apply.disabled).toBe(true)
    await type(document.body.querySelector<HTMLInputElement>('input[placeholder="Miktar"]')!, '7')
    expect(apply.disabled).toBe(false)
    await click(apply)
    expect(sent).toEqual([])
    expect(bar()?.textContent).toContain('2 Değişiklik')
    expect(document.body.querySelectorAll('button.rv-draftbar-save')).toHaveLength(1)
    expect(document.body.textContent).not.toContain('Tümüne Uygula')
    expect(document.body.querySelector('.rv-draftbar')).toBeNull()
  })

  // Y3 (scan 23 Nov 2026): Uygula twice in a row (Liste, then Satış) dropped the first draft's list price and refused the
  // sale price against the saved list price. The second Uygula works over the first one's draft.
  it('a second Uygula keeps the first one\'s draft and checks against it', async () => {
    await render('price')
    await pickValue('Panel', 'Liste')
    await type(bulkField('Liste Fiyatı (₺)'), '1.000')
    await click(applyButton())
    await pickValue('Panel', 'Satış')
    await type(bulkField('Satış Fiyatı (₺)'), '800')
    await click(applyButton())
    expect(document.body.querySelector('[role="alert"]')).toBeNull()
    expect(bar()?.textContent).toContain('2 Değişiklik')
    await click(barButton('Kaydet'))
    await click(barButton('Onayla ve Kaydet'))
    expect(sent.filter(call => call.path.endsWith('/default-prices')).map(call => [call.path, call.body])).toEqual([
      ['/variants/v1/default-prices', { listPrice: 1000, salePrice: 800, reason: 'Hızlı varyant fiyat düzenleme' }],
      ['/variants/v2/default-prices', { listPrice: 1000, salePrice: 800, reason: 'Hızlı varyant fiyat düzenleme' }],
    ])
  })

  it('a second Uygula still refuses a sale price above the drafted list price, keeping the first draft', async () => {
    await render('price')
    await pickValue('Panel', 'Liste')
    await type(bulkField('Liste Fiyatı (₺)'), '600')
    await click(applyButton())
    await pickValue('Panel', 'Satış')
    await type(bulkField('Satış Fiyatı (₺)'), '700')
    await click(applyButton())
    expect(document.body.querySelector('[role="alert"]')?.textContent).toContain('liste fiyatı satış fiyatından küçük olamaz')
    await click(barButton('Kaydet'))
    await click(barButton('Onayla ve Kaydet'))
    expect(sent.filter(call => call.path.endsWith('/default-prices')).map(call => call.body)).toEqual([
      { listPrice: 600, salePrice: 450, reason: 'Hızlı varyant fiyat düzenleme' },
      { listPrice: 600, salePrice: 450, reason: 'Hızlı varyant fiyat düzenleme' },
    ])
  })

  // O4 (scan 23 Nov 2026): Bu Kadar Ekle applied twice added once, since it worked on the saved stock, not the draft.
  it('Bu Kadar Ekle applied twice adds twice, and Kaydet sends the sum against the saved stock', async () => {
    await render()
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Stok İşlemi"]')!.click() })
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).find(item => item.textContent?.startsWith('Bu Kadar Ekle'))!.click() })
    await type(document.body.querySelector<HTMLInputElement>('input[placeholder="Miktar"]')!, '5')
    await click(applyButton())
    await click(applyButton())
    expect(stockBox('SKU-1').value).toBe('13')
    await act(async () => { document.body.querySelector<HTMLButtonElement>('button[aria-label^="Stok İşlemi"]')!.click() })
    await act(async () => { Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]')).find(item => item.textContent?.startsWith('Bu Kadar Çıkar'))!.click() })
    await type(document.body.querySelector<HTMLInputElement>('input[placeholder="Miktar"]')!, '2')
    await click(applyButton())
    expect(stockBox('SKU-1').value).toBe('11')
    await click(barButton('Kaydet'))
    await click(barButton('Onayla ve Kaydet'))
    expect(sent.map(call => [call.path, call.body.onHand, call.body.expectedOnHand])).toEqual([['/inventory/v1/on-hand', 11, 3], ['/inventory/v2/on-hand', 11, 3]])
  })

  // O1 (scan 23 Nov 2026): Özel Fiyat on for only some variants. Shown as mixed, a following variant shows the panel price
  // (what goes), and nothing is a change until the owner switches or types.
  const mixedProduct = (): Product => ({ ...product, variants: product.variants.map(item => ({ ...item, platformStatuses: [testVariantPlatformStatus({ platform: 'Trendyol', platformCode: 'TRENDYOL', connectionId: 'c1', offerId: `o-${item.id}`, offerVersion: 1, listPrice: item.id === 'v1' ? 600 : 520, salePrice: item.id === 'v1' ? 550 : 470, currency: 'TRY', followsPanelPrice: item.id === 'v1' ? false : true })] })) })
  it('shows a mixed marketplace as mixed and has nothing to save untouched', async () => {
    await render('price', [{ id: 'c1', displayName: 'Trendyol', platformCode: 'TRENDYOL' }], [mixedProduct()])
    await pickValue('Trendyol', 'Satış')
    const toggle = document.body.querySelector<HTMLInputElement>('input.rr-qt-switch')!
    expect(toggle.indeterminate).toBe(true)
    expect(toggle.closest('label')?.textContent).toContain('Özel Fiyat · 1/2 Varyant')
    expect(document.body.querySelector<HTMLInputElement>('input[aria-label="SKU-1 Trendyol satış fiyatı"]')?.value).toBe('550,00')
    expect(document.body.querySelector('input[aria-label="SKU-2 Trendyol satış fiyatı"]')).toBeNull()
    expect(Array.from(document.body.querySelectorAll('.rr-mx-follow')).map(item => item.textContent)).toEqual(['450,00'])
    expect(bar()?.textContent).toContain('Değişiklik Yok')
    expect(barButton('Kaydet').disabled).toBe(true)
    await click(footButton('Kapat'))
    expect(sent).toEqual([])
  })
  it('switching a mixed marketplace on is a draft that turns only the following variants own', async () => {
    await render('price', [{ id: 'c1', displayName: 'Trendyol', platformCode: 'TRENDYOL' }], [mixedProduct()])
    await pickValue('Trendyol', 'Satış')
    await click(document.body.querySelector<HTMLInputElement>('input.rr-qt-switch')!)
    expect(bar()?.textContent).toContain('1 Değişiklik')
    await click(barButton('Kaydet'))
    await click(barButton('Onayla ve Kaydet'))
    expect(sent.map(call => [call.path, call.method, call.body])).toEqual([['/channel-offers/panel-price-follow', 'PUT', { connectionId: 'c1', variantIds: ['v2'], followsPanelPrice: false }]])
  })

  it('names a price falling below half in Kaydet\'s confirmation and still saves on Onayla ve Kaydet', async () => {
    await render('price')
    const sale = document.body.querySelector<HTMLInputElement>('input[aria-label^="SKU-1"][aria-label$="satış fiyatı"]')!
    await act(async () => { sale.focus() })
    await type(sale, '4,5')
    await click(barButton('Kaydet'))
    expect(bar()?.querySelector('[role="alert"]')?.textContent).toBe('1 Varyantın Fiyatı Yarıdan Fazla Düşüyor: 450,00 → 4,50')
    await click(barButton('Onayla ve Kaydet'))
    expect(sent.filter(call => call.path.endsWith('/default-prices')).map(call => call.body)).toEqual([{ listPrice: 500, salePrice: 4.5, reason: 'Hızlı varyant fiyat düzenleme' }])
  })

  it('names no warning for an ordinary price change', async () => {
    await render('price')
    const sale = document.body.querySelector<HTMLInputElement>('input[aria-label^="SKU-1"][aria-label$="satış fiyatı"]')!
    await act(async () => { sale.focus() })
    await type(sale, '440')
    await click(barButton('Kaydet'))
    expect(bar()?.querySelector('[role="alert"]')).toBeNull()
  })

  it('Enter in a cell never submits the window, and each variant\'s tick is reachable by keyboard', async () => {
    await render()
    const enter = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })
    await act(async () => { stockBox('SKU-2').dispatchEvent(enter) })
    expect(enter.defaultPrevented).toBe(true)
    const tick = document.body.querySelector<HTMLInputElement>('input[aria-label$="varyantını seç"]')!
    expect(tick.tabIndex).toBe(0)
  })

  it('closing without a draft closes at once', async () => {
    await render()
    await click(footButton('Kapat'))
    expect(closed).toBe(1)
  })
})
