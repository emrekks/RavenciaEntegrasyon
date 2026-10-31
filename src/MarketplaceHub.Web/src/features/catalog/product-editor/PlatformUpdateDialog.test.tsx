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

function render(selectedIds: string[], fields: PlatformUpdateField[], openGroups: PlatformUpdateGroupKey[] = allGroups, onFieldsChange = vi.fn(), onOpenGroupsChange = vi.fn()) {
  host = document.createElement('div')
  document.body.append(host)
  root = createRoot(host)
  act(() => root?.render(<PlatformUpdateDialog targets={[target('ty', 'TRENDYOL'), target('hb', 'HEPSIBURADA'), target('sh', 'SHOPIFY', false)]} selectedIds={selectedIds} onToggle={vi.fn()} fields={fields} onFieldsChange={onFieldsChange} openGroups={openGroups} onOpenGroupsChange={onOpenGroupsChange} submitting={false} onClose={vi.fn()} onConfirm={vi.fn()} />))
  return { onFieldsChange, onOpenGroupsChange }
}

const fieldLabels = () => [...document.querySelectorAll('.platform-update-field:not(.is-linked) > span')].map(node => node.textContent)
const card = (store: string) => [...document.querySelectorAll('.publish-platform-card')].find(item => item.textContent?.includes(store))
const chipsOf = (store: string) => [...card(store)?.querySelectorAll('.platform-update-send-list li') ?? []].map(node => node.textContent)
const sendsOf = (store: string) => card(store)?.querySelector('.platform-update-sends')?.textContent ?? ''

describe('PlatformUpdateDialog', () => {
  it('shows only the fields the selected stores take, grouped', () => {
    render(['hb'], ['TITLE', 'PRICE'])
    expect([...document.querySelectorAll('.platform-update-group-check strong')].map(node => node.textContent)).toEqual(['Ürün Bilgileri', 'Kargo ve Teslimat', 'Satış Bilgileri'])
    expect(fieldLabels()).toContain('Garanti Süresi')
    expect(fieldLabels()).not.toContain('Menşei')
    expect(fieldLabels()).not.toContain('Teslimat Süresi')
    act(() => root?.unmount()); root = undefined
    render(['ty'], ['TITLE', 'PRICE'])
    expect(fieldLabels()).not.toContain('Garanti Süresi')
    expect(fieldLabels()).toContain('Teslimat Süresi')
  })

  it('shows a linked field as a locked box that follows the field it rides with', () => {
    render(['ty'], ['ATTRIBUTES'])
    const linked = document.querySelector<HTMLLabelElement>('.platform-update-field.is-linked')!
    expect(linked.textContent).toBe('Renk / BedenKategori Özellikleri ile')
    const box = linked.querySelector('input')!
    expect(box.disabled).toBe(true)
    expect(box.checked).toBe(true)
    // A linked box is not a pick of the group.
    expect(document.querySelector('.platform-update-group-check small')?.textContent).toBe('1/4 seçili')
  })

  it('names the information that is never changed or never sent', () => {
    render(['ty'], ['TITLE'])
    const fixed = document.querySelector('.platform-update-fixed')!
    expect(fixed.querySelector('summary')?.textContent).toBe('Değişmeyen ve Gönderilmeyen Bilgiler')
    expect([...fixed.querySelectorAll('dt')].map(node => node.textContent)).toEqual(['Barkod', 'Stok Kodu', 'Marka, Kategori, Model Kodu', 'Gönderilmeyenler'])
  })

  it('lists on each card exactly what goes, including what rides along', () => {
    render(['ty', 'hb'], ['TITLE', 'PRICE'])
    expect(chipsOf('TRENDYOL Mağaza')).toEqual(['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Renk / Beden', 'Liste Fiyatı', 'Satış Fiyatı'])
    expect(sendsOf('TRENDYOL Mağaza')).toContain('aynı istekte birlikte gider: Açıklama, Görseller, Kategori Özellikleri')
    expect(chipsOf('HEPSIBURADA Mağaza')).toEqual(['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Renk / Beden', 'Garanti Süresi', 'Desi', 'KDV Oranı', 'Barkod', 'Satış Fiyatı'])
    expect(sendsOf('SHOPIFY Mağaza')).toContain('—')
  })

  it('keeps the bar out of the scrolling body, as the window footer', () => {
    render(['ty'], ['TITLE'])
    expect(document.querySelector('.platform-update-modal > footer.publish-selection-footer')).not.toBeNull()
    expect(document.querySelector('.product-publish-popup-body .publish-selection-footer')).toBeNull()
  })

  it('blocks Güncelle when nothing picked goes to the selected stores', () => {
    render(['hb'], ['DELIVERY'])
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
})
