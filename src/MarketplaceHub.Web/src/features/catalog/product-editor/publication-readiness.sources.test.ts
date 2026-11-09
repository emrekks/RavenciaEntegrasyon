import { QueryClient } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'

// The mapping check's reads go through the query client: the runs while the editor's form settles (other selections,
// other keys) and the bulk check read each source once, and a save that invalidates the check reads it again. The
// fields' value mappings and value lists are read in one request each, not one per field, and the category list only
// for the mapped category.
const calls = vi.hoisted(() => [] as string[])

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string) => {
    calls.push(path)
    if (path.startsWith('/mappings/categories/')) return { externalId: '411', snapshotId: 's1', status: 'VERIFIED' }
    if (path.startsWith('/reference-data/categories?')) return { snapshotId: 's1', items: path.includes('externalId=411') ? [{ externalId: '411', name: 'Elbise', isActive: true, isLeaf: true }] : [] }
    // Kumaş (21) has no value list yet: the batch leaves it out.
    if (path.startsWith('/reference-data/categories/411/attribute-values?')) return { 20: { snapshotId: 'v1', items: [{ externalId: '301', isActive: true }] }, 22: { snapshotId: 'v3', items: [{ externalId: '501', isActive: true }] } }
    if (path.startsWith('/reference-data/categories/411/attributes')) return { snapshotId: 'a1', items: [{ externalId: '20', name: 'Renk', isActive: true, isRequired: false }, { externalId: '21', name: 'Kumaş', isActive: true, isRequired: false }, { externalId: '22', name: 'Beden', isActive: true, isRequired: true }] }
    if (path.startsWith('/mappings/attributes?')) return [{ localId: 'a-renk', externalId: '20', snapshotId: 'a1', status: 'VERIFIED' }, { localId: 'a-kumas', externalId: '21', snapshotId: 'a1', status: 'VERIFIED' }, { localId: 'a-beden', externalId: '22', snapshotId: 'a1', status: 'VERIFIED' }]
    if (path.startsWith('/mappings/attribute-values/batch?')) return { 20: [], 21: [], 22: [{ localId: 's', externalId: '501', snapshotId: 'v3', status: 'VERIFIED' }] }
    if (path.endsWith('/attribute-defaults')) return []
    return null
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { loadPublicationAttributeIssues } from './publication-readiness'

const connection = { id: 'ty', platformCode: 'TRENDYOL' }
const selection = (attributeId: string, name: string, values: string[], isRequired = false) => ({ attributeId, name, isRequired, values: values.map(id => ({ id, label: id })), hasCustomValue: false })
const renk = (values: string[]) => [selection('a-renk', 'Renk', values)]
const all = [selection('a-renk', 'Renk', ['red']), selection('a-kumas', 'Kumaş', ['cotton']), selection('a-beden', 'Beden', ['s'], true)]
const pathOf = (path: string) => path.split('?')[0]

beforeEach(() => { calls.length = 0 })

describe('loadPublicationAttributeIssues sources', () => {
  it('reads each source once across runs with the same fields', async () => {
    const client = new QueryClient()
    await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', [])
    await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', renk(['red']))
    await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', renk(['red', 'blue']))
    const counts = calls.map(pathOf).reduce<Record<string, number>>((total, path) => ({ ...total, [path]: (total[path] ?? 0) + 1 }), {})
    expect(Object.values(counts).every(count => count === 1)).toBe(true)
    expect(counts['/mappings/categories/c1']).toBe(1)
    expect(counts['/reference-data/categories']).toBe(1)
    expect(counts['/reference-data/categories/411/attributes']).toBe(1)
    expect(counts['/mappings/attributes']).toBe(1)
    expect(counts['/mappings/attribute-values/batch']).toBe(1)
    expect(counts['/reference-data/categories/411/attribute-values']).toBe(1)
    expect(counts['/connections/ty/attribute-defaults']).toBe(1)
  })

  it('reads the mapped category only, and every field in one request per source', async () => {
    const client = new QueryClient()
    const result = await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', all)
    expect(calls).toContain('/reference-data/categories?connectionId=ty&externalId=411')
    expect(calls).toContain('/mappings/attribute-values/batch?connectionId=ty&categoryExternalId=411&attributeIds=20,21,22')
    expect(calls).toContain('/reference-data/categories/411/attribute-values?connectionId=ty&attributeIds=20,21,22')
    // No per-field reads.
    expect(calls.filter(path => /\/attributes\/[^/]+\/values|scopeExternalId=411%2F/.test(path))).toEqual([])
    expect(calls).toHaveLength(7)
    expect(result.categoryScope).toBe('411')
    expect(result.categoryMapping).toBeUndefined()
  })

  it('reads a category the store list lacks as a stale mapping', async () => {
    const { hubApi } = await import('../../../shared/api')
    vi.mocked(hubApi).mockImplementationOnce(async path => { calls.push(path); return { externalId: '999', snapshotId: 's1', status: 'VERIFIED' } })
    const result = await loadPublicationAttributeIssues(new QueryClient(), connection, 'Trendyol', 'c2', all)
    expect(calls).toContain('/reference-data/categories?connectionId=ty&externalId=999')
    expect(result.categoryMapping).toBe('stale')
  })

  it('reads again after the check is invalidated (a mapping was saved)', async () => {
    const client = new QueryClient()
    await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', all)
    await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
    await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', all)
    expect(calls.filter(path => path === '/mappings/categories/c1?connectionId=ty')).toHaveLength(2)
    expect(calls.filter(path => path.startsWith('/mappings/attribute-values/batch'))).toHaveLength(2)
  })
})
