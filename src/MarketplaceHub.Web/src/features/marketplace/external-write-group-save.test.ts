import { beforeEach, describe, expect, it, vi } from 'vitest'

const state = vi.hoisted(() => ({ groupRoute: true, calls: [] as Array<{ path: string; ifMatch: string | null; body: unknown }>, open: 0, maxOpen: 0 }))

vi.mock('../../shared/api', async importOriginal => {
  const original = await importOriginal<typeof import('../../shared/api')>()
  return {
    ...original,
    hubApi: vi.fn(async (path: string, init?: RequestInit) => {
      state.calls.push({ path, ifMatch: new Headers(init?.headers).get('If-Match'), body: JSON.parse(String(init?.body)) })
      if (path.endsWith('/external-write-group') && !state.groupRoute) throw new original.ApiRequestError('İşlem tamamlanamadı (404).', 404)
      state.open++; state.maxOpen = Math.max(state.maxOpen, state.open)
      await new Promise(resolve => setTimeout(resolve, 5))
      state.open--
      return []
    })
  }
})

import { saveExternalWriteGroup } from './external-write-group-save'

const pair = [
  { resourceType: 'PRICE_WRITE', version: 3, intervalSeconds: 0, overlapSeconds: 0, jitterSeconds: 0 },
  { resourceType: 'STOCK_WRITE', version: 4, intervalSeconds: 0, overlapSeconds: 0, jitterSeconds: 0 }
]

beforeEach(() => { state.groupRoute = true; state.calls = []; state.open = 0; state.maxOpen = 0 })

describe('saveExternalWriteGroup', () => {
  it('sends the pair as one request with each version', async () => {
    await saveExternalWriteGroup('c1', pair, true)
    expect(state.calls).toEqual([{ path: '/connections/c1/external-write-group', ifMatch: null, body: { enabled: true, policies: [{ resourceType: 'PRICE_WRITE', version: 3 }, { resourceType: 'STOCK_WRITE', version: 4 }] } }])
  })

  // While a deploy rolls out, an API without the group route: one policy after the other, never in parallel.
  it('falls back to one PUT after the other on an API without the group route', async () => {
    state.groupRoute = false
    await saveExternalWriteGroup('c1', pair, true)
    expect(state.calls.slice(1).map(call => [call.path, call.ifMatch])).toEqual([['/connections/c1/sync-policies/PRICE_WRITE', '"v3"'], ['/connections/c1/sync-policies/STOCK_WRITE', '"v4"']])
    expect(state.maxOpen).toBe(1)
  })
})
