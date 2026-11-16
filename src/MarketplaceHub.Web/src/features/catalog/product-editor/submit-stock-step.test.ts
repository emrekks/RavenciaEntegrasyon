import { beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ calls: [] as Array<{ path: string; body: Record<string, unknown> }>, refuse: new Set<string>() }))
vi.mock('../../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../../shared/api')>()
  return {
    ...original,
    hubApi: vi.fn(async (path: string, init?: RequestInit) => {
      api.calls.push({ path, body: JSON.parse(String(init?.body)) })
      if (api.refuse.has(path)) throw new original.ApiRequestError('Stok bu arada değişti.', 409)
      return {}
    })
  }
})

import { runStockStep } from './submit-stock-step'
import type { Product } from '../shared'
import type { VariantDraft } from './editor-types'

const row = (key: string, stock: number, loadedStock?: number): VariantDraft => ({ key, optionSignature: key, options: {}, attributeValueIds: {}, sku: `SKU-${key}`, barcode: '', stock, loadedStock, salePrice: 1, listPrice: 1, costPrice: null, mediaRefs: [] })
const variants = (ids: string[]) => ids.map(id => ({ id, sku: `SKU-${id}` })) as unknown as Product['variants']

beforeEach(() => { api.calls.length = 0; api.refuse.clear() })

describe('runStockStep', () => {
  it('sends only the changed stocks, against the stock the form loaded', async () => {
    const result = await runStockStep([row('v1', 7, 3), row('v2', 5, 5), row('new', 4)], variants(['v1', 'v2', 'new']))
    expect(api.calls).toEqual([{ path: '/inventory/v1/on-hand', body: expect.objectContaining({ onHand: 7, expectedOnHand: 3, reason: 'Ürün düzenleme stoğu' }) }])
    expect(result.saved).toHaveLength(1)
    expect(result.warning).toBeNull()
  })
  it('names a refused row and goes on with the others', async () => {
    api.refuse.add('/inventory/v1/on-hand')
    const result = await runStockStep([row('v1', 7, 3), row('v2', 6, 5)], variants(['v1', 'v2']))
    expect(api.calls.map(call => call.path)).toEqual(['/inventory/v1/on-hand', '/inventory/v2/on-hand'])
    expect(result.warning).toMatch(/^Stok kaydedilemedi: SKU-v1: /)
  })
})
