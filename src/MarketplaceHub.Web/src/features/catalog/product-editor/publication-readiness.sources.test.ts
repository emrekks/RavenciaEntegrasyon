import { QueryClient } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'

// The mapping check's reads go through the query client: the runs while the editor's form settles (other selections,
// other keys) and the bulk check read each source once, and a save that invalidates the check reads it again.
const calls = vi.hoisted(() => [] as string[])

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string) => {
    calls.push(path.split('?')[0])
    if (path.startsWith('/mappings/categories/')) return { externalId: '411', snapshotId: 's1', status: 'VERIFIED' }
    if (path.startsWith('/reference-data/categories?')) return { snapshotId: 's1', items: [{ externalId: '411', name: 'Elbise', isActive: true, isLeaf: true }] }
    if (path.startsWith('/reference-data/categories/411/attributes/20/values')) return { snapshotId: 'v1', items: [{ externalId: '301', isActive: true }] }
    if (path.startsWith('/reference-data/categories/411/attributes')) return { snapshotId: 'a1', items: [{ externalId: '20', name: 'Renk', isActive: true, isRequired: false }] }
    if (path.startsWith('/mappings/attributes?')) return [{ localId: 'a-renk', externalId: '20', snapshotId: 'a1', status: 'VERIFIED' }]
    if (path.startsWith('/mappings/attribute-values?')) return []
    if (path.endsWith('/attribute-defaults')) return []
    return null
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { loadPublicationAttributeIssues } from './publication-readiness'

const connection = { id: 'ty', platformCode: 'TRENDYOL' }
const renk = (values: string[]) => [{ attributeId: 'a-renk', name: 'Renk', isRequired: false, values: values.map(id => ({ id, label: id })), hasCustomValue: false }]

beforeEach(() => { calls.length = 0 })

describe('loadPublicationAttributeIssues sources', () => {
  it('reads each source once across runs with other selections', async () => {
    const client = new QueryClient()
    await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', [])
    await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', renk(['red']))
    await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', renk(['red', 'blue']))
    const counts = calls.reduce<Record<string, number>>((all, path) => ({ ...all, [path]: (all[path] ?? 0) + 1 }), {})
    expect(Object.values(counts).every(count => count === 1)).toBe(true)
    expect(counts['/mappings/categories/c1']).toBe(1)
    expect(counts['/reference-data/categories']).toBe(1)
    expect(counts['/reference-data/categories/411/attributes']).toBe(1)
    expect(counts['/mappings/attributes']).toBe(1)
    expect(counts['/connections/ty/attribute-defaults']).toBe(1)
  })

  it('reads again after the check is invalidated (a mapping was saved)', async () => {
    const client = new QueryClient()
    await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', [])
    await client.invalidateQueries({ queryKey: ['publication-attribute-readiness'] })
    await loadPublicationAttributeIssues(client, connection, 'Trendyol', 'c1', [])
    expect(calls.filter(path => path === '/mappings/categories/c1')).toHaveLength(2)
  })
})
