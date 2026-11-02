import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '../../../test/render'

// 2 Nov 2026 (live, n11 Tunik): Tüm Değerleri Eşle saved the Beden values, but the open value table kept its first copy:
// the rows stayed "Seçin…", Kaydet lit up and pressing it removed the values the button had just saved.
type ValueMapping = { id: string; localId: string; externalId: string; version: number; snapshotId: string; status: string; scopeExternalId: string; connectionId: string }
const state = vi.hoisted(() => ({ valueMappings: [] as ValueMapping[], writes: [] as string[] }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const tunik = { id: 'tunik', name: 'Tunik', path: 'Tunik', isActive: true, isLeaf: true }
  const beden = { id: 'beden', name: 'Beden', code: 'attribute-beden', isActive: true, roles: [], values: [{ id: 'v-s', value: 'S', isActive: true }, { id: 'v-m', value: 'M', isActive: true }, { id: 'v-26', value: '26', isActive: true }] }
  const categoryMapping = { id: 'cm', connectionId: 'n11', localId: 'tunik', externalId: '1209242', scopeExternalId: '', status: 'VERIFIED', snapshotId: 'cats', version: 1 }
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    if (method === 'PUT' && path.startsWith('/mappings/attribute-values/')) {
      const localId = path.split('/')[3]
      const body = JSON.parse(String(init?.body)) as { externalId: string; snapshotId: string; scopeExternalId: string }
      state.writes.push(`PUT ${localId}`)
      const saved = { id: `m-${localId}`, localId, externalId: body.externalId, version: 1, snapshotId: body.snapshotId, status: 'VERIFIED', scopeExternalId: body.scopeExternalId, connectionId: 'n11' }
      state.valueMappings = [...state.valueMappings.filter(item => item.localId !== localId), saved]
      return saved
    }
    if (method !== 'GET') { state.writes.push(`${method} ${path}`); return true }
    if (path.startsWith('/mappings/categories/tunik')) return categoryMapping
    if (path.startsWith('/mappings/categories')) return [categoryMapping]
    if (path.startsWith('/catalog/categories/tunik/attribute-requirements')) return []
    if (path.startsWith('/reference-data/categories/1209242/attributes/1494/values')) return { snapshotId: 'beden-values', resourceType: 'ATTRIBUTE_VALUES', items: [{ externalId: 'n-s', name: 'S', isActive: true }, { externalId: 'n-m', name: 'M', isActive: true }, { externalId: 'n-xl', name: 'XL', isActive: true }] }
    if (path.startsWith('/reference-data/categories/1209242/attributes')) return { snapshotId: 'attrs', resourceType: 'CATEGORY_ATTRIBUTES', items: [{ externalId: '1494', name: 'Beden', isActive: true, isRequired: true, allowsCustomValue: false }] }
    if (path.startsWith('/reference-data/categories')) return { snapshotId: 'cats', items: [{ externalId: '1209242', name: 'Tesettür Tunik', path: 'Kadın Giyim & Aksesuar / Tesettür Giyim / Tesettür Tunik', isActive: true, isLeaf: true }] }
    if (path.startsWith('/mappings/attribute-values')) return state.valueMappings.map(item => ({ ...item }))
    if (path.startsWith('/mappings/attributes')) return [{ id: 'am', connectionId: 'n11', localId: 'beden', externalId: '1494', scopeExternalId: '1209242', status: 'VERIFIED', snapshotId: 'attrs', version: 1 }]
    if (path.startsWith('/mappings/attribute-name-hints')) return {}
    if (path.startsWith('/mappings/conflicts')) return { sameCategory: [], crossCategory: [] }
    if (path.includes('/attribute-defaults')) return []
    return null
  }
  return {
    ...original,
    hubApi: vi.fn(respond),
    loadAllPages: vi.fn(async (path: string) => ({ items: path.startsWith('/catalog/categories') ? [tunik] : [beden] }))
  }
})

import { ProductCategoryFieldsDialog } from './MappingPages'

afterEach(() => { cleanup(); state.writes = [] })

const settle = async () => { for (let index = 0; index < 6; index++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)) }) }
const button = (text: string) => [...document.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent?.trim() === text)
const rowTarget = (value: string) => [...document.querySelectorAll('.cm-vrow')].find(row => row.querySelector('.cm-pv')?.textContent === value)?.querySelector('.vp-trigger')?.textContent?.trim()
const toast = () => document.body.textContent ?? ''

async function openBeden() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><ProductCategoryFieldsDialog platformCode="N11" connectionId="n11" localCategoryId="tunik" focus={{ items: [], initialExternalAttributeId: '1494' }} onClose={() => undefined} /></QueryClientProvider>)
  await waitFor(() => document.querySelector('.cm-vtable'))
  await settle()
}

describe('Tüm Değerleri Eşle', () => {
  it('shows the saved values in the open table and leaves nothing for Kaydet to undo', async () => {
    state.valueMappings = [{ id: 'm-v-s', localId: 'v-s', externalId: 'n-s', version: 1, snapshotId: 'beden-values', status: 'VERIFIED', scopeExternalId: '1209242/1494', connectionId: 'n11' }]
    await openBeden()
    expect(document.querySelector('.cm-vtools-title span')?.textContent).toBe('1 / 3')
    expect(document.querySelector('.cm-item.is-selected .cm-meta')?.textContent).toBe('1/3 değer')

    await act(async () => { button('Tüm Değerleri Eşle')!.click() })
    await settle()

    expect(state.writes).toEqual(['PUT v-m'])
    expect(document.querySelector('.cm-vtools-title span')?.textContent).toBe('2 / 3')
    expect(rowTarget('M')).toBe('M')
    expect(document.querySelector('.cm-item.is-selected .cm-meta')?.textContent).toBe('2/3 değer')
    // the table matches the server, so there is nothing unsaved and Kaydet stays off
    expect(document.querySelector('.cm-foot')?.classList.contains('is-dirty')).toBe(false)
    expect(button('Kaydet')?.disabled).toBe(true)
    expect(toast()).toContain('1 başlıkta 1 değer aynı adlı karşılığıyla eşlendi ve kaydedildi.')
    expect(toast()).toContain('1 başlıkta 1 değerin aynı adlı n11 karşılığı yok (Beden 1).')
  })

  it('says 0 values were matched and why when nothing has a same-named counterpart', async () => {
    state.valueMappings = ['s', 'm'].map(code => ({ id: `m-v-${code}`, localId: `v-${code}`, externalId: `n-${code}`, version: 1, snapshotId: 'beden-values', status: 'VERIFIED', scopeExternalId: '1209242/1494', connectionId: 'n11' }))
    await openBeden()
    await act(async () => { button('Tüm Değerleri Eşle')!.click() })
    await settle()
    expect(state.writes).toEqual([])
    expect(toast()).toContain('0 değer eşlendi (değişiklik yok): 1 başlıkta 1 değerin aynı adlı n11 karşılığı yok (Beden 1). Bunları alanlarının tablosundan elle eşleyin.')
  })
})
