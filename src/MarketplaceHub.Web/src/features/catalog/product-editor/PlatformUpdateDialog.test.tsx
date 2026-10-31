import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PlatformUpdateDialog, type PlatformUpdateTarget } from './PlatformUpdateDialog'
import type { PlatformUpdateField, PlatformUpdateGroupKey } from './platform-update-fields'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let root: Root | undefined
let host: HTMLDivElement | undefined

afterEach(() => {
  if (root) act(() => root?.unmount())
  host?.remove()
  document.body.innerHTML = ''
  root = undefined
  host = undefined
})

const target = (id: string, platformCode: string, writable = true): PlatformUpdateTarget => ({
  connection: { id, platformCode, displayName: `${platformCode} Mağaza`, externalStoreId: `${id}-store`, status: 'ACTIVE' },
  capabilities: { writable, content: writable }
})

const allGroups: PlatformUpdateGroupKey[] = ['product', 'shipping', 'sales']

function render(selectedIds: string[], fields: PlatformUpdateField[], openGroups: PlatformUpdateGroupKey[] = allGroups, onFieldsChange = vi.fn(), onOpenGroupsChange = vi.fn(), onSelectedIdsChange = vi.fn(), onConfirm = vi.fn()) {
  host = document.createElement('div')
  document.body.append(host)
  root = createRoot(host)
  act(() => root?.render(<PlatformUpdateDialog targets={[target('ty', 'TRENDYOL'), target('hb', 'HEPSIBURADA'), target('sh', 'SHOPIFY', false), target('pz', 'PAZARAMA')]} selectedIds={selectedIds} onSelectedIdsChange={onSelectedIdsChange} fields={fields} onFieldsChange={onFieldsChange} openGroups={openGroups} onOpenGroupsChange={onOpenGroupsChange} submitting={false} onClose={vi.fn()} onConfirm={onConfirm} />))
  return { onFieldsChange, onOpenGroupsChange, onSelectedIdsChange, onConfirm }
}

const fieldLabels = () => [...document.querySelectorAll('.platform-update-field:not(.is-linked) > span')].map(node => node.textContent)
const card = (store: string) => [...document.querySelectorAll('.publish-platform-card')].find(item => item.textContent?.includes(store))
const chipsOf = (store: string) => [...card(store)?.querySelectorAll('.platform-update-chips li') ?? []].map(node => node.textContent)
const extraChipsOf = (store: string) => [...card(store)?.querySelectorAll('.platform-update-chips li.is-extra') ?? []].map(node => node.textContent)
const sendsOf = (store: string) => card(store)?.querySelector('.platform-update-sends')?.textContent ?? ''
const bulk = (section: number, label: string) => [...document.querySelectorAll('.platform-update-bulk')[section]?.querySelectorAll<HTMLButtonElement>('button') ?? []].find(button => button.textContent === label)!
const groupCount = (index: number) => document.querySelectorAll('.platform-update-group-check small')[index]?.textContent

describe('PlatformUpdateDialog', () => {
  it('shows only the fields the selected stores take, grouped, without naming stores beside a field (the cards below say what goes where)', () => {
    render(['hb'], ['TITLE', 'PRICE'])
    expect([...document.querySelectorAll('.platform-update-group-check strong')].map(node => node.textContent)).toEqual(['Ürün Bilgileri', 'Kargo ve Teslimat', 'Satış Bilgileri'])
    expect(fieldLabels()).toContain('Garanti Süresi')
    expect(fieldLabels()).toContain('Teslimat Süresi')
    act(() => root?.unmount()); root = undefined
    render(['hb', 'ty'], ['TITLE', 'PRICE'])
    expect(fieldLabels()).toContain('Teslimat Süresi')
    expect(fieldLabels()).not.toContain('Menşei')
    expect(document.querySelector('.platform-update-field:not(.is-linked) small')).toBeNull()
    act(() => root?.unmount()); root = undefined
    // Pazarama takes the VAT rate, price and stock: the content and shipping fields are Trendyol's alone beside it.
    render(['pz'], ['VAT_RATE'])
    expect(fieldLabels()).toEqual(['Fiyat', 'Stok', 'KDV Oranı'])
    act(() => root?.unmount()); root = undefined
    render(['ty', 'pz'], ['TITLE', 'PRICE'])
  })

  it('shows a linked field as a locked box that follows the field it rides with', () => {
    render(['ty'], ['ATTRIBUTES'])
    const linked = [...document.querySelectorAll<HTMLLabelElement>('.platform-update-field.is-linked')]
    expect(linked.map(node => node.textContent)).toEqual(['Renk / BedenKategori Özellikleri ile', 'MenşeiKategori Özellikleri ile'])
    const box = linked[0].querySelector('input')!
    expect(box.disabled).toBe(true)
    expect(box.checked).toBe(true)
    // A linked box is not a pick of the group.
    expect(groupCount(0)).toBe('1/4 seçili')
  })

  it('counts and sends only the fields shown', () => {
    const { onConfirm } = render(['ty'], ['TITLE', 'DELIVERY', 'PRICE', 'WARRANTY'])
    expect(groupCount(0)).toBe('1/4 seçili')
    act(() => root?.unmount()); root = undefined
    const hepsiburada = render(['hb'], ['TITLE', 'DELIVERY', 'PRICE'], allGroups, vi.fn(), vi.fn(), vi.fn(), onConfirm)
    expect(groupCount(0)).toBe('1/5 seçili')
    act(() => [...document.querySelectorAll('button')].find(button => button.textContent === 'Güncelle')!.click())
    expect(hepsiburada.onConfirm).toHaveBeenLastCalledWith(['TITLE', 'DELIVERY', 'PRICE'])
    act(() => root?.unmount()); root = undefined
    const pazarama = render(['pz'], ['TITLE', 'DELIVERY', 'PRICE', 'VAT_RATE'])
    act(() => [...document.querySelectorAll('button')].find(button => button.textContent === 'Güncelle')!.click())
    expect(pazarama.onConfirm).toHaveBeenLastCalledWith(['PRICE', 'VAT_RATE'])
  })

  it('ticks a field a newly selected store adds, and drops one no selected store takes any more', () => {
    const { onFieldsChange, onSelectedIdsChange } = render(['pz'], ['PRICE'])
    const trendyolCard = [...document.querySelectorAll<HTMLButtonElement>('.publish-platform-card-head')].find(button => button.textContent?.includes('TRENDYOL'))!
    act(() => trendyolCard.click())
    expect(onSelectedIdsChange).toHaveBeenLastCalledWith(['pz', 'ty'])
    expect(onFieldsChange).toHaveBeenLastCalledWith(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'DESI', 'DELIVERY', 'PRICE'])
    act(() => root?.unmount()); root = undefined
    const second = render(['hb', 'ty'], ['TITLE', 'WARRANTY', 'DELIVERY', 'PRICE'])
    act(() => [...document.querySelectorAll<HTMLButtonElement>('.publish-platform-card-head')].find(button => button.textContent?.includes('HEPSIBURADA'))!.click())
    expect(second.onFieldsChange).toHaveBeenLastCalledWith(['TITLE', 'DELIVERY', 'PRICE'])
  })

  it('lists on each card exactly what goes, including what rides along', () => {
    render(['ty', 'hb'], ['TITLE', 'PRICE'])
    expect(chipsOf('TRENDYOL Mağaza')).toEqual(['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Liste Fiyatı', 'Satış Fiyatı', 'Renk / Beden', 'Menşei'])
    expect(extraChipsOf('TRENDYOL Mağaza')).toEqual(['Renk / Beden', 'Menşei'])
    expect(card('TRENDYOL Mağaza')?.querySelector('.platform-update-sends dd')?.getAttribute('title')).toBe('Ürün Adı, Açıklama, Görseller, Kategori Özellikleri, Liste Fiyatı, Satış Fiyatı, Renk / Beden, Menşei')
    expect(sendsOf('TRENDYOL Mağaza')).toContain('aynı istekte birlikte gider: Açıklama, Görseller, Kategori Özellikleri')
    expect(chipsOf('HEPSIBURADA Mağaza')).toEqual(['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Garanti Süresi', 'Desi', 'KDV Oranı', 'Satış Fiyatı', 'Renk / Beden', 'Menşei', 'Barkod'])
    expect(chipsOf('SHOPIFY Mağaza')).toEqual([])
    expect(sendsOf('SHOPIFY Mağaza')).toContain('—')
    act(() => root?.unmount()); root = undefined
    // A picked field the store does not take stays on its card, struck through, after what goes.
    render(['ty', 'pz'], ['TITLE', 'PRICE'])
    expect(chipsOf('PAZARAMA Mağaza')).toEqual(['Liste Fiyatı', 'Satış Fiyatı', 'Stok', 'Ürün Adı'])
    expect([...card('PAZARAMA Mağaza')!.querySelectorAll('.platform-update-chips li.is-skipped s')].map(node => node.textContent)).toEqual(['Ürün Adı'])
    expect(card('TRENDYOL Mağaza')!.querySelector('.is-skipped')).toBeNull()
  })

  it('keeps the bar out of the scrolling body, as the window footer', () => {
    render(['ty'], ['TITLE'])
    expect(document.querySelector('.platform-update-modal > footer.publish-selection-footer')).not.toBeNull()
    expect(document.querySelector('.product-publish-popup-body .publish-selection-footer')).toBeNull()
  })

  it('blocks Güncelle when nothing picked goes to the selected stores', () => {
    render(['pz'], ['DELIVERY'])
    const update = [...document.querySelectorAll('button')].find(button => button.textContent === 'Güncelle')!
    expect(update.disabled).toBe(true)
    expect(document.body.textContent).toContain('En az bir bilgi seçin')
  })

  it('ticks a whole group, shows a partly picked group as mixed, and folds a group away', () => {
    const { onFieldsChange, onOpenGroupsChange } = render(['ty'], ['TITLE'])
    const groupBoxes = [...document.querySelectorAll<HTMLInputElement>('.platform-update-group-check input')]
    expect(groupBoxes[0].indeterminate).toBe(true)
    expect(groupBoxes[2].checked).toBe(false)
    act(() => groupBoxes[2].click())
    expect(onFieldsChange).toHaveBeenLastCalledWith(['TITLE', 'PRICE', 'STOCK', 'VAT_RATE'])
    const toggle = document.querySelector<HTMLButtonElement>('.platform-update-group-toggle')!
    expect(toggle.getAttribute('aria-expanded')).toBe('true')
    act(() => toggle.click())
    expect(onOpenGroupsChange).toHaveBeenLastCalledWith(['shipping', 'sales'])
  })

  it('keeps a folded group folded', () => {
    render(['ty'], ['TITLE'], ['sales'])
    expect(document.querySelectorAll('.platform-update-group-body')).toHaveLength(1)
    expect(fieldLabels()).toEqual(['Fiyat', 'Stok', 'KDV Oranı'])
  })

  it('parts the fields from the stores under their own titles', () => {
    render(['ty'], ['TITLE'])
    expect([...document.querySelectorAll('.platform-update-section-head h3')].map(node => node.textContent)).toEqual(['Gönderilecek Bilgiler', 'Platformlar'])
    expect(document.querySelectorAll('.platform-update-section-head small')[1]?.textContent).toBe('1/3 mağaza seçili · 1 mağaza güncellenemez')
  })

  it('picks every shown field or none, leaving the linked fields to follow theirs', () => {
    const { onFieldsChange } = render(['ty'], ['TITLE', 'STOCK'])
    act(() => bulk(0, 'Tümünü Seç').click())
    expect(onFieldsChange).toHaveBeenLastCalledWith(['TITLE', 'STOCK', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'DESI', 'DELIVERY', 'PRICE', 'VAT_RATE'])
    act(() => bulk(0, 'Temizle').click())
    expect(onFieldsChange).toHaveBeenLastCalledWith([])
    act(() => root?.unmount()); root = undefined
    // A field no selected store takes is kept for later: only the shown ones are cleared.
    render(['pz'], ['TITLE', 'PRICE'], allGroups, onFieldsChange)
    act(() => bulk(0, 'Temizle').click())
    expect(onFieldsChange).toHaveBeenLastCalledWith(['TITLE'])
  })

  it('greys out a bulk field pick that would change nothing, and blocks Güncelle with nothing picked', () => {
    render(['pz'], ['PRICE', 'STOCK', 'VAT_RATE'])
    expect(bulk(0, 'Tümünü Seç').disabled).toBe(true)
    expect(bulk(0, 'Temizle').disabled).toBe(false)
    act(() => root?.unmount()); root = undefined
    render(['pz'], [])
    expect(bulk(0, 'Temizle').disabled).toBe(true)
    expect([...document.querySelectorAll('button')].find(button => button.textContent === 'Güncelle')!.disabled).toBe(true)
  })

  it('picks every store that can be updated, or none, and ticks the fields they bring', () => {
    const { onSelectedIdsChange, onFieldsChange } = render(['pz'], ['PRICE'])
    act(() => bulk(1, 'Tümünü Seç').click())
    // Shopify is read-only here: it is never picked.
    expect(onSelectedIdsChange).toHaveBeenLastCalledWith(['ty', 'hb', 'pz'])
    expect(onFieldsChange).toHaveBeenLastCalledWith(['TITLE', 'DESCRIPTION', 'IMAGES', 'ATTRIBUTES', 'WARRANTY', 'DESI', 'DELIVERY', 'PRICE'])
    act(() => bulk(1, 'Hiçbirini Seçme').click())
    expect(onSelectedIdsChange).toHaveBeenLastCalledWith([])
    act(() => root?.unmount()); root = undefined
    render(['ty', 'hb', 'pz'], ['PRICE'])
    expect(bulk(1, 'Tümünü Seç').disabled).toBe(true)
    act(() => root?.unmount()); root = undefined
    render([], ['PRICE'])
    expect(bulk(1, 'Hiçbirini Seçme').disabled).toBe(true)
  })
})
