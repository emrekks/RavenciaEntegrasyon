import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '../../../test/render'

// 7 Nov 2026 (owner): switching fields in Kategori Alanları ve Değerleri made the right side jump: the open field gave way
// to a loading splash, then to the new table. The window now reads the whole category's value progress once, prefetches
// a field's lists, and keeps the open field (dimmed) until the chosen one has them.
const state = vi.hoisted(() => ({ calls: [] as string[], releaseKalip: null as null | (() => void) }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const tunik = { id: 'tunik', name: 'Tunik', path: 'Tunik', isActive: true, isLeaf: true }
  const values = (prefix: string) => ['1', '2', '3'].map(code => ({ id: `${prefix}-${code}`, value: `${prefix.toUpperCase()} ${code}`, isActive: true }))
  const beden = { id: 'beden', name: 'Beden', code: 'attribute-beden', isActive: true, roles: [], values: values('b') }
  const kalip = { id: 'kalip', name: 'Kalıp', code: 'attribute-kalip', isActive: true, roles: [], values: values('k') }
  const categoryMapping = { id: 'cm', connectionId: 'n11', localId: 'tunik', externalId: '900', scopeExternalId: '', status: 'VERIFIED', snapshotId: 'cats', version: 1 }
  const list = (prefix: string) => ({ snapshotId: `${prefix}-values`, resourceType: 'ATTRIBUTE_VALUES', items: ['1', '2', '3'].map(code => ({ externalId: `${prefix}-x${code}`, name: `${prefix.toUpperCase()} ${code}`, isActive: true })) })
  const respond = async (path: string) => {
    state.calls.push(path)
    if (path.startsWith('/mappings/categories/tunik')) return categoryMapping
    if (path.startsWith('/mappings/categories')) return [categoryMapping]
    if (path.startsWith('/catalog/categories/tunik/attribute-requirements')) return []
    if (path.startsWith('/reference-data/categories/900/attributes/11/values')) return list('b')
    if (path.startsWith('/reference-data/categories/900/attributes/12/values')) { await new Promise<void>(resolve => { state.releaseKalip = resolve }); return list('k') }
    if (path.startsWith('/reference-data/categories/900/attributes')) return { snapshotId: 'attrs', resourceType: 'CATEGORY_ATTRIBUTES', items: [{ externalId: '11', name: 'Beden', isActive: true, isRequired: true, allowsCustomValue: false }, { externalId: '12', name: 'Kalıp', isActive: true, isRequired: true, allowsCustomValue: false }] }
    if (path.startsWith('/reference-data/categories')) return { snapshotId: 'cats', items: [{ externalId: '900', name: 'Tunik', path: 'Giyim / Tunik', isActive: true, isLeaf: true }] }
    if (path.startsWith('/mappings/attribute-value-progress')) return { '11': ['b-1'], '12': ['k-1', 'k-2'] }
    if (path.startsWith('/mappings/attribute-values')) return path.includes('900%2F11') ? [{ id: 'm1', localId: 'b-1', externalId: 'b-x1', version: 1, snapshotId: 'b-values', status: 'VERIFIED', scopeExternalId: '900/11', connectionId: 'n11' }] : []
    if (path.startsWith('/mappings/attributes')) return [
      { id: 'a1', connectionId: 'n11', localId: 'beden', externalId: '11', scopeExternalId: '900', status: 'VERIFIED', snapshotId: 'attrs', version: 1 },
      { id: 'a2', connectionId: 'n11', localId: 'kalip', externalId: '12', scopeExternalId: '900', status: 'VERIFIED', snapshotId: 'attrs', version: 1 }
    ]
    if (path.startsWith('/mappings/attribute-name-hints')) return {}
    if (path.startsWith('/mappings/conflicts')) return { sameCategory: [], crossCategory: [] }
    if (path.includes('/attribute-defaults')) return []
    return null
  }
  return {
    ...original,
    hubApi: vi.fn(respond),
    loadAllPages: vi.fn(async (path: string) => ({ items: path.startsWith('/catalog/categories') ? [tunik] : [beden, kalip] }))
  }
})

import { ProductCategoryFieldsDialog } from './MappingPages'

afterEach(() => { cleanup(); state.calls = []; state.releaseKalip = null })

const settle = async () => { for (let index = 0; index < 6; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) }) }
const item = (name: string) => [...document.querySelectorAll<HTMLButtonElement>('.cm-item')].find(entry => entry.querySelector('.cm-name')?.textContent === name)!
const detailTitle = () => document.querySelector('.cm-detail .cm-detail-head h4')?.textContent

describe('switching fields in the category window', () => {
  it('counts every field from one progress read and keeps the open field until the chosen one has its values', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><ProductCategoryFieldsDialog platformCode="N11" connectionId="n11" localCategoryId="tunik" focus={{ items: [], initialExternalAttributeId: '11' }} onClose={() => undefined} /></QueryClientProvider>)

    await waitFor(() => document.querySelector('.cm-vtable'))
    await settle()

    // one progress read for the category; no value list of the field that is not open
    expect(state.calls.filter(path => path.startsWith('/mappings/attribute-value-progress'))).toHaveLength(1)
    expect(state.calls).not.toContain('/mappings/attribute-values?connectionId=n11&scopeExternalId=900%2F12')
    expect(item('Kalıp').querySelector('.cm-meta')?.textContent).toBe('2/3 değer')
    expect(detailTitle()).toBe('Beden')

    await act(async () => { item('Kalıp').click() })
    await settle()
    // Kalıp's list is still on its way: Beden stays, dimmed, and the chosen item is already marked
    expect(detailTitle()).toBe('Beden')
    expect(document.querySelector('.cm-detail')?.classList.contains('is-pending')).toBe(true)
    expect(item('Kalıp').classList.contains('is-selected')).toBe(true)
    expect(document.querySelector('.cm-detail .rv-loading-feedback')).toBeNull()

    await act(async () => { state.releaseKalip?.() })
    await settle()
    expect(detailTitle()).toBe('Kalıp')
    expect(document.querySelector('.cm-detail')?.classList.contains('is-pending')).toBe(false)
    expect(document.querySelectorAll('.cm-vscroll .cm-vrow')).toHaveLength(3)
    expect(document.querySelector('.cm-vtools-title span')?.textContent).toBe('0 / 3')

    // back to Beden: in the cache, so it opens at once
    await act(async () => { item('Beden').click() })
    expect(detailTitle()).toBe('Beden')
    expect(document.querySelector('.cm-vtools-title span')?.textContent).toBe('1 / 3')
  })
})
