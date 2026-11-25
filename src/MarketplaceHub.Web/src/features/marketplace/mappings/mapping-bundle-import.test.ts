import { beforeEach, describe, expect, it, vi } from 'vitest'

type Call = { method: string; path: string; ifMatch: string | null; body: unknown }
const api = vi.hoisted(() => ({ calls: [] as Call[], refusePut: new Set<string>() }))

const mapping = (localId: string, externalId: string, extra: Record<string, unknown> = {}) => ({ id: `m-${localId}`, connectionId: 'c1', snapshotId: 's1', localId, scopeExternalId: '', externalId, status: 'VERIFIED', verifiedAt: null, version: 2, ...extra })

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return {
    ...original,
    hubApi: vi.fn(async (path: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET'
      api.calls.push({ method, path, ifMatch: new Headers(init?.headers).get('If-Match'), body: init?.body ? JSON.parse(String(init.body)) : null })
      if (method === 'PUT' && api.refusePut.has(path)) throw new original.ApiRequestError('Eşleme bu sırada değişti.', 409)
      if (method === 'PUT') return {}
      if (path.startsWith('/mappings/categories?')) return [mapping('k-same', 'ext-same'), mapping('k-other', 'ext-held'), mapping('k-old', 'ext-old', { snapshotId: 's0', version: 5 })]
      if (path.startsWith('/mappings/attributes?')) return []
      if (path.startsWith('/mappings/attribute-values?')) return []
      if (path === '/catalog/attributes/a-renk/values') return { id: 'a-renk', code: 'option-renk', name: 'Renk', dataType: 'SINGLE_SELECT', roles: ['OPTION'], values: [{ id: 'v-siyah', value: 'Siyah' }, { id: 'v-mavi-new', value: 'Mavi' }] }
      if (path.includes('/attributes/remote-renk/values?')) return { snapshotId: 'sv1', items: [{ externalId: 'r-mavi' }, { externalId: 'r-siyah' }] }
      if (path.includes('/attributes?')) return { snapshotId: 'sa1', items: [{ externalId: 'remote-renk' }] }
      return null
    })
  }
})

import { importMappingBundle, mappingImportStep, mappingImportSummary, type MappingTransferBundle } from './mapping-bundle-import'
import type { LocalAttribute, LocalCategory } from './mapping-pages/mapping-shared'

const category = (id: string, name: string) => ({ id, name, path: name, depth: 0, parentId: null, isLeaf: true, isActive: true, version: 1 }) as unknown as LocalCategory
const categories = ['k-same', 'k-other', 'k-old', 'k-new', 'k-refused'].map(id => category(id, `Kategori ${id}`))
const renk = { id: 'a-renk', code: 'option-renk', name: 'Renk', dataType: 'SINGLE_SELECT', roles: ['OPTION'], values: [{ id: 'v-siyah', value: 'Siyah' }] } as unknown as LocalAttribute
const bundle: MappingTransferBundle = {
  format: 'RAVENCIA_MAPPING_BUNDLE', version: 1, exportedAt: '2026-11-25T00:00:00Z',
  categories,
  attributes: [{ ...renk, values: [{ id: 'src-siyah', value: 'Siyah' }, { id: 'src-mavi', value: 'Mavi' }] } as unknown as LocalAttribute],
  categoryMappings: [mapping('k-same', 'ext-same'), mapping('k-other', 'ext-file'), mapping('k-refused', 'ext-r'), mapping('k-new', 'ext-new'), mapping('k-old', 'ext-old')] as never,
  attributeMappings: [],
  attributeValueMappings: [mapping('src-mavi', 'r-mavi', { scopeExternalId: 'cat-1/remote-renk' })] as never
}
const selection = { categories: true, options: true, attributes: true, mappings: true }

beforeEach(() => { api.calls.length = 0; api.refusePut.clear() })

describe('mapping backup import (A-7)', () => {
  it('reads each mapping first: skips an identical one, lists a different one, writes a stale one at its version and goes on past a refusal', async () => {
    api.refusePut.add('/mappings/categories/k-refused')
    const result = await importMappingBundle({ bundle, selection, connectionId: 'c1', referenceSnapshotId: 's1', categories, attributes: [renk] })
    const puts = api.calls.filter(call => call.method === 'PUT').map(call => [call.path, call.ifMatch, (call.body as { externalId: string }).externalId])
    expect(puts).toEqual([
      ['/mappings/categories/k-refused', null, 'ext-r'],
      ['/mappings/categories/k-new', null, 'ext-new'],
      ['/mappings/categories/k-old', '"v5"', 'ext-old'],
      // The value added from the file is mapped through the id the POST answered with.
      ['/mappings/attribute-values/v-mavi-new', null, 'r-mavi']
    ])
    expect(api.calls.find(call => call.path === '/catalog/attributes/a-renk/values')?.body).toEqual([{ value: 'Mavi', sortOrder: 1 }])
    expect(result).toEqual({ written: 3, skipped: 1, conflicts: ['Kategori k-other'], failures: ['Kategori k-refused: Eşleme bu sırada değişti.'] })
    expect(mappingImportSummary(result)).toEqual({
      kind: 'warning',
      message: '3 eşleme yazıldı, 1 atlandı, 1 çakışma. Panelde farklı eşlemesi olduğu için değiştirilmedi: Kategori k-other. 1 kayıt yazılamadı: Kategori k-refused: Eşleme bu sırada değişti.'
    })
  })

  it('decides one mapping from what the panel holds', () => {
    expect(mappingImportStep(undefined, 'x', 's1')).toEqual({ write: true, ifMatch: null })
    expect(mappingImportStep(mapping('l', 'x'), 'x', 's1')).toEqual({ write: false, reason: 'same' })
    expect(mappingImportStep(mapping('l', 'y'), 'x', 's1')).toEqual({ write: false, reason: 'conflict' })
    expect(mappingImportStep(mapping('l', 'x', { status: 'SUGGESTED', version: 7 }), 'x', 's1')).toEqual({ write: true, ifMatch: '"v7"' })
  })

  it('is red only when nothing was written and something failed, green when all went', () => {
    expect(mappingImportSummary({ written: 0, skipped: 0, conflicts: [], failures: ['A: x'] }).kind).toBe('error')
    expect(mappingImportSummary({ written: 2, skipped: 1, conflicts: [], failures: [] })).toEqual({ kind: 'success', message: '2 eşleme yazıldı, 1 atlandı, 0 çakışma.' })
  })
})
