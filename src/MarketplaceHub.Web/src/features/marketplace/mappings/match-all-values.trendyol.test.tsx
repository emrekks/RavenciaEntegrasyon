import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '../../../test/render'

// 2 Nov 2026 (live, Trendyol Tunik): Beden stayed at 4/21 although panel "L" sits next to Trendyol "L".
type ValueMapping = { id: string; localId: string; externalId: string; version: number; snapshotId: string; status: string; scopeExternalId: string; connectionId: string }
const state = vi.hoisted(() => ({ valueMappings: [] as ValueMapping[], writes: [] as string[] }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const tunik = { id: 'tunik', name: 'Tunik', path: 'Tunik', isActive: true, isLeaf: true }
  // the live panel Beden: a Trendyol-imported code and an OPTION requirement in the category
  const beden = { id: 'beden', name: 'Beden', code: 'TRD_590_338', isActive: true, roles: [], values: ['XL', 'L', 'M', '36-38', 'ılık', 'Tek Ebat'].map(value => ({ id: `v-${value}`, value, isActive: true })) }
  const categoryMapping = { id: 'cm', connectionId: 'ty', localId: 'tunik', externalId: '605', scopeExternalId: '', status: 'VERIFIED', snapshotId: 'cats', version: 1 }
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    if (method === 'PUT' && path.startsWith('/mappings/attribute-values/')) {
      const localId = decodeURIComponent(path.split('/')[3])
      const body = JSON.parse(String(init?.body)) as { externalId: string; snapshotId: string; scopeExternalId: string }
      state.writes.push(`PUT ${localId}`)
      const saved = { id: `m-${localId}`, localId, externalId: body.externalId, version: 1, snapshotId: body.snapshotId, status: 'VERIFIED', scopeExternalId: body.scopeExternalId, connectionId: 'ty' }
      state.valueMappings = [...state.valueMappings.filter(item => item.localId !== localId), saved]
      return saved
    }
    if (method !== 'GET') { state.writes.push(`${method} ${path}`); return true }
    if (path.startsWith('/mappings/categories/tunik')) return categoryMapping
    if (path.startsWith('/mappings/categories')) return [categoryMapping]
    if (path.startsWith('/catalog/categories/tunik/attribute-requirements')) return [{ categoryId: 'tunik', attributeId: 'beden', role: 'OPTION', isRequired: true, attribute: beden }]
    if (path.startsWith('/reference-data/categories/605/attributes/338/values')) return { snapshotId: 'beden-values', resourceType: 'ATTRIBUTE_VALUES', items: ['XL', 'L', '36/38', '36', '36 - 38', 'S', 'M', 'ILIK'].map(name => ({ externalId: `t-${name}`, name, isActive: true })) }
    if (path.startsWith('/reference-data/categories/605/attributes')) return { snapshotId: 'attrs', resourceType: 'CATEGORY_ATTRIBUTES', items: [{ externalId: '338', name: 'Beden', isActive: true, isRequired: true, allowsCustomValue: false }] }
    if (path.startsWith('/reference-data/categories')) return { snapshotId: 'cats', items: [{ externalId: '605', name: 'Tunik', path: 'Giyim / Üst Giyim / Bluz & Tunik / Tunik', isActive: true, isLeaf: true }] }
    if (path.startsWith('/mappings/attribute-values')) return state.valueMappings.map(item => ({ ...item }))
    if (path.startsWith('/mappings/attributes')) return [{ id: 'am', connectionId: 'ty', localId: 'beden', externalId: '338', scopeExternalId: '605', status: 'VERIFIED', snapshotId: 'attrs', version: 1 }]
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

describe('Tüm Değerleri Eşle on Trendyol', () => {
  it('matches Beden by name: L to L, 36-38 to 36 - 38, Turkish case', async () => {
    state.valueMappings = [{ id: 'm-v-XL', localId: 'v-XL', externalId: 't-XL', version: 1, snapshotId: 'beden-values', status: 'VERIFIED', scopeExternalId: '605/338', connectionId: 'ty' }]
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><ProductCategoryFieldsDialog platformCode="TRENDYOL" connectionId="ty" localCategoryId="tunik" focus={{ items: [], initialExternalAttributeId: '338' }} onClose={() => undefined} /></QueryClientProvider>)
    await waitFor(() => document.querySelector('.cm-vtable'))
    await settle()
    await act(async () => { button('Tüm Değerleri Eşle')!.click() })
    await settle()
    expect(state.valueMappings.map(item => `${item.localId}=${item.externalId}`).sort()).toEqual(['v-36-38=t-36 - 38', 'v-L=t-L', 'v-M=t-M', 'v-XL=t-XL', 'v-ılık=t-ILIK'].sort())
    expect(rowTarget('L')).toBe('L')
    expect(document.body.textContent).toContain('aynı adlı Trendyol karşılığı yok (Beden: Tek Ebat)')
  })
})
