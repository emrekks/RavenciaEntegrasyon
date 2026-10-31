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

function render(selectedIds: string[], fields: PlatformUpdateField[], openGroups: PlatformUpdateGroupKey[] = ['product', 'sales'], onFieldsChange = vi.fn(), onOpenGroupsChange = vi.fn(), onToggle = vi.fn(), onConfirm = vi.fn()) {
  host = document.createElement('div')
  document.body.append(host)
  root = createRoot(host)
  act(() => root?.render(<PlatformUpdateDialog targets={[target('ty', 'TRENDYOL'), target('hb', 'HEPSIBURADA'), target('sh', 'SHOPIFY', false)]} selectedIds={selectedIds} onToggle={onToggle} fields={fields} onFieldsChange={onFieldsChange} openGroups={openGroups} onOpenGroupsChange={onOpenGroupsChange} submitting={false} onClose={vi.fn()} onConfirm={onConfirm} />))
  return { onFieldsChange, onOpenGroupsChange, onToggle, onConfirm }
}

const fieldLabels = () => [...document.querySelectorAll('.platform-update-field > span')].map(node => node.textContent)
const chipsOf = (store: string) => [...([...document.querySelectorAll('.publish-platform-card')].find(card => card.textContent?.includes(store))?.querySelectorAll('.platform-update-chips li') ?? [])].map(node => node.textContent)
const sendsOf = (store: string) => [...document.querySelectorAll('.publish-platform-card')].find(card => card.textContent?.includes(store))?.querySelector('.platform-update-sends')?.textContent ?? ''

describe('PlatformUpdateDialog', () => {
  it('shows only the fields the selected stores take, grouped', () => {
    render(['hb'], ['TITLE', 'PRICE'])
    expect([...document.querySelectorAll('.platform-update-group-check strong')].map(node => node.textContent)).toEqual(['Ürün Bilgileri', 'Satış Bilgileri'])
    expect(fieldLabels()).not.toContain('Teslimat Süresi')
    act(() => root?.unmount()); root = undefined
    render(['hb', 'ty'], ['TITLE', 'PRICE'])
    expect(fieldLabels()).toContain('Teslimat Süresi')
    expect(fieldLabels()).not.toContain('Menşei')
    // Taken by Trendyol only: the field says so.
    const delivery = [...document.querySelectorAll('.platform-update-field')].find(node => node.textContent?.startsWith('Teslimat Süresi'))
    expect(delivery?.querySelector('.platform-update-field-only')?.textContent).toBe('Yalnız Trendyol')
    expect([...document.querySelectorAll('.platform-update-field-only')]).toHaveLength(1)
  })

  it('counts and sends only the fields shown', () => {
    const { onConfirm } = render(['ty'], ['TITLE', 'DELIVERY', 'PRICE'])
    expect(document.querySelector('.platform-update-group-check small')?.textContent).toBe('2/7 seçili')
    act(() => root?.unmount()); root = undefined
    const hepsiburada = render(['hb'], ['TITLE', 'DELIVERY', 'PRICE'], ['product', 'sales'], vi.fn(), vi.fn(), vi.fn(), onConfirm)
    expect(document.querySelector('.platform-update-group-check small')?.textContent).toBe('1/6 seçili')
    act(() => [...document.querySelectorAll('button')].find(button => button.textContent === 'Güncelle')!.click())
    expect(hepsiburada.onConfirm).toHaveBeenLastCalledWith(['TITLE', 'PRICE'])
  })

  it('ticks a field a newly selected store adds, and drops one no selected store takes any more', () => {
    const { onFieldsChange, onToggle } = render(['hb'], ['TITLE', 'PRICE'])
    const trendyolCard = [...document.querySelectorAll<HTMLButtonElement>('.publish-platform-card-head')].find(button => button.textContent?.includes('TRENDYOL'))!
    act(() => trendyolCard.click())
    expect(onToggle).toHaveBeenLastCalledWith('ty')
    expect(onFieldsChange).toHaveBeenLastCalledWith(['TITLE', 'DELIVERY', 'PRICE'])
    act(() => root?.unmount()); root = undefined
    const second = render(['hb', 'ty'], ['TITLE', 'DELIVERY', 'PRICE'])
    act(() => [...document.querySelectorAll<HTMLButtonElement>('.publish-platform-card-head')].find(button => button.textContent?.includes('TRENDYOL'))!.click())
    expect(second.onFieldsChange).toHaveBeenLastCalledWith(['TITLE', 'PRICE'])
  })

  it('tells each card what really goes, including what rides along', () => {
    render(['ty', 'hb'], ['TITLE', 'PRICE'])
    // The tooltip keeps the whole list as a sentence.
    expect(document.querySelector('.publish-platform-card .platform-update-sends dd')?.getAttribute('title')).toBe('Ürün Adı, Açıklama, Görseller, Kategori Özellikleri, Fiyat')
    expect(sendsOf('TRENDYOL Mağaza')).toContain('aynı istekte birlikte gider: Açıklama, Görseller, Kategori Özellikleri')
    expect(chipsOf('HEPSIBURADA Mağaza')).toEqual(['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Desi', 'KDV Oranı', 'Fiyat', 'Garanti Süresi', 'Barkod', 'Video'])
    expect(chipsOf('TRENDYOL Mağaza')).toEqual(['Ürün Adı', 'Açıklama', 'Görseller', 'Kategori Özellikleri', 'Fiyat'])
    expect(chipsOf('SHOPIFY Mağaza')).toEqual([])
    expect(document.querySelector('.platform-update-group-hint')).toBeNull()
    expect(sendsOf('SHOPIFY Mağaza')).toContain('—')
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
    expect(groupBoxes[1].checked).toBe(false)
    act(() => groupBoxes[1].click())
    expect(onFieldsChange).toHaveBeenLastCalledWith(['TITLE', 'PRICE', 'STOCK'])
    const toggle = document.querySelector<HTMLButtonElement>('.platform-update-group-toggle')!
    expect(toggle.getAttribute('aria-expanded')).toBe('true')
    act(() => toggle.click())
    expect(onOpenGroupsChange).toHaveBeenLastCalledWith(['sales'])
  })

  it('keeps a folded group folded', () => {
    render(['ty'], ['TITLE'], ['sales'])
    expect(document.querySelectorAll('.platform-update-group-body')).toHaveLength(1)
    expect(fieldLabels()).toEqual(['Fiyat', 'Stok'])
  })
})
