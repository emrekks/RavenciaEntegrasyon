import { QueryClient } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'

// Owner decision 14 Nov 2026: a Shopify card needs its category mapping (the store's product type) like every other
// marketplace; Shopify has no category fields, so nothing past the mapping is read.
const calls = vi.hoisted(() => [] as string[])

vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  const respond = async (path: string) => {
    calls.push(path)
    if (path.startsWith('/mappings/categories/c-bluz')) return { externalId: 'Bluz', snapshotId: 's1', status: 'VERIFIED' }
    if (path.startsWith('/mappings/categories/')) return null
    if (path.startsWith('/reference-data/categories?')) return { snapshotId: 's1', items: path.includes('externalId=Bluz') ? [{ externalId: 'Bluz', name: 'Bluz', isActive: true, isLeaf: true }] : [] }
    throw new Error(`unexpected read ${path}`)
  }
  return { ...original, hubApi: vi.fn(respond) }
})

import { loadPublicationAttributeIssues, missingPublicationChecks, type PublicationProductCheck } from './publication-readiness'

const shopify = { id: 'sh', platformCode: 'SHOPIFY' }

beforeEach(() => { calls.length = 0 })

describe('Shopify publication readiness', () => {
  it('blocks a category without a Shopify product type', async () => {
    const result = await loadPublicationAttributeIssues(new QueryClient(), shopify, 'Shopify', 'c-anne', [], 'Anne Bluz')
    expect(result.categoryMapping).toBe('missing')
    expect(result.requiredIssues).toEqual([{ attribute: 'Kategori eşlemesi', detail: 'Anne Bluz kategorisinin Shopify karşılığı seçilmemiş; kategoriyi eşleyin.' }])
  })

  it('passes a mapped category and reads no category fields', async () => {
    const result = await loadPublicationAttributeIssues(new QueryClient(), shopify, 'Shopify', 'c-bluz', [], 'Bluz')
    expect(result).toEqual({ requiredIssues: [], optionalWarnings: [], categoryScope: 'Bluz' })
    expect(calls.every(path => path.startsWith('/mappings/categories/') || path.startsWith('/reference-data/categories?'))).toBe(true)
  })

  it('asks a Shopify card for a category', () => {
    const checks: PublicationProductCheck[] = [{ title: 'Kategori ve Seçenekler', detail: 'x', ok: false, shopifyOk: false, shopifyDetail: 'Shopify yayını için ürün kategorisi seçilmeli.' }]
    expect(missingPublicationChecks(checks, 'SHOPIFY').map(check => check.detail)).toEqual(['Shopify yayını için ürün kategorisi seçilmeli.'])
  })
})
