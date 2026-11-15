import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '../../../test/render'

// WEB-SAYFALAR-6: a Kaydet the server partly refuses keeps the refused choices, and their rows are marked "Kaydedilemedi"
// in the error tone until the user changes them; the rows that were saved are not marked.
const state = vi.hoisted(() => ({ writes: [] as string[] }))

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const tunik = { id: 'tunik', name: 'Tunik', path: 'Tunik', isActive: true, isLeaf: true }
  const beden = { id: 'beden', name: 'Beden', code: 'attribute-beden', isActive: true, roles: [], values: ['1', '2', '3'].map(code => ({ id: `b-${code}`, value: `B ${code}`, isActive: true })) }
  const categoryMapping = { id: 'cm', connectionId: 'n11', localId: 'tunik', externalId: '900', scopeExternalId: '', status: 'VERIFIED', snapshotId: 'cats', version: 1 }
  const list = { snapshotId: 'b-values', resourceType: 'ATTRIBUTE_VALUES', items: ['1', '2', '3'].map(code => ({ externalId: `b-x${code}`, name: `X ${code}`, isActive: true })) }
  const respond = async (path: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    if (method === 'PUT' && path.startsWith('/mappings/attribute-values/')) {
      state.writes.push(`PUT ${path}`)
      // B 2's pair is refused (another window changed it); B 1's goes through.
      if (path.startsWith('/mappings/attribute-values/b-2')) throw new original.ApiRequestError('Eşleme başka bir oturumda değişti.', 409, 'CONFLICT')
      return { id: 'm-new', connectionId: 'n11', snapshotId: 'b-values', localId: 'b-1', scopeExternalId: '900/11', externalId: 'b-x1', status: 'VERIFIED', version: 1 }
    }
    if (path.startsWith('/mappings/categories/tunik')) return categoryMapping
    if (path.startsWith('/mappings/categories')) return [categoryMapping]
    if (path.startsWith('/catalog/categories/tunik/attribute-requirements')) return []
    if (path.startsWith('/reference-data/categories/900/attributes/11/values')) return list
    if (path.startsWith('/reference-data/categories/900/attributes')) return { snapshotId: 'attrs', resourceType: 'CATEGORY_ATTRIBUTES', items: [{ externalId: '11', name: 'Beden', isActive: true, isRequired: true, allowsCustomValue: false }] }
    if (path.startsWith('/reference-data/categories')) return { snapshotId: 'cats', items: [{ externalId: '900', name: 'Tunik', path: 'Giyim / Tunik', isActive: true, isLeaf: true }] }
    if (path.startsWith('/mappings/attribute-value-progress')) return { '11': [] }
    if (path.startsWith('/mappings/attribute-values')) return state.writes.length ? [{ id: 'm-new', localId: 'b-1', externalId: 'b-x1', version: 1, snapshotId: 'b-values', status: 'VERIFIED', scopeExternalId: '900/11', connectionId: 'n11' }] : []
    if (path.startsWith('/mappings/attributes')) return [{ id: 'a1', connectionId: 'n11', localId: 'beden', externalId: '11', scopeExternalId: '900', status: 'VERIFIED', snapshotId: 'attrs', version: 1 }]
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
const row = (value: string) => [...document.querySelectorAll<HTMLElement>('.cm-vscroll .cm-vrow')].find(entry => entry.querySelector('.cm-pv')?.firstChild?.textContent === value)!
const button = (text: string) => [...document.querySelectorAll<HTMLButtonElement>('button')].find(entry => entry.textContent?.trim() === text)
async function pick(value: string, option: string) {
  await act(async () => { row(value).querySelector<HTMLButtonElement>('.vp-trigger')!.click() })
  await act(async () => { [...document.querySelectorAll<HTMLButtonElement>('.vp-list [role="option"]')].find(entry => entry.textContent === option)!.click() })
}

describe('a partly refused Kaydet of value mappings', () => {
  it('marks the refused rows, keeps their choice and clears the mark when the row changes', async () => {
    // The picker scrolls its active option into view; jsdom has no layout.
    Element.prototype.scrollIntoView = vi.fn()
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><ProductCategoryFieldsDialog platformCode="N11" connectionId="n11" localCategoryId="tunik" focus={{ items: [], initialExternalAttributeId: '11' }} onClose={() => undefined} /></QueryClientProvider>)
    await waitFor(() => document.querySelector('.cm-vtable'))
    await settle()

    await pick('B 1', 'X 1')
    await pick('B 2', 'X 2')
    expect(document.querySelectorAll('.cm-vrow.is-save-failed')).toHaveLength(0)
    await act(async () => { button('Kaydet')!.click() })
    await settle()

    expect(state.writes).toHaveLength(2)
    // B 2 was refused: still chosen, marked in the error tone; B 1 was saved and is not marked.
    expect(row('B 2').classList.contains('is-save-failed')).toBe(true)
    expect(row('B 2').querySelector('.cm-failed-tag')?.textContent).toBe('Kaydedilemedi')
    expect(row('B 2').querySelector('.vp-trigger')?.textContent?.trim()).toBe('X 2')
    expect(row('B 1').classList.contains('is-save-failed')).toBe(false)

    // The user picks again for B 2: the row is theirs, the mark goes.
    await pick('B 2', 'X 3')
    expect(row('B 2').classList.contains('is-save-failed')).toBe(false)
    expect(row('B 2').querySelector('.cm-failed-tag')).toBeNull()
  })
})
