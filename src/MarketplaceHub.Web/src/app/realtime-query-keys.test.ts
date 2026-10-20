import { describe, expect, it } from 'vitest'
import { realtimeQueryKeysByResource, realtimeQueryKeysFor, realtimeReconnectQueryKeys } from './realtime-query-keys'

// Every source file of the app, as text: a mapped root without a query that uses it refreshes nothing.
const sources = import.meta.glob<string>(['../**/*.ts', '../**/*.tsx', '!../**/*.test.ts', '!../**/*.test.tsx', '!./realtime-query-keys.ts'], { query: '?raw', import: 'default', eager: true })
const producers = new Set<string>()
for (const text of Object.values(sources)) for (const match of text.matchAll(/queryKey:\s*\[\s*'([^']+)'/g)) producers.add(match[1])

describe('realtime refresh map', () => {
  it('reads the app sources', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(50)
    expect(producers.has('orders')).toBe(true)
  })

  it.each(Object.entries(realtimeQueryKeysByResource))('every key of “%s” has a query that uses it', (_resource, keys) => {
    const missing = keys.map(key => key[0]).filter(root => !producers.has(root))
    expect(missing).toEqual([])
  })

  it('covers every resource the server announces', () => {
    // AppDbContext.OutboxChange and the job broadcaster; questions are mapped for when the server starts announcing them.
    for (const resource of ['orders', 'products', 'inventory', 'returns', 'invoices', 'connections', 'jobs', 'questions']) expect(realtimeQueryKeysByResource[resource]?.length).toBeGreaterThan(0)
  })

  it('refreshes stock on the product screens and invoices on the invoice screens', () => {
    const roots = (resource: string) => realtimeQueryKeysFor([resource]).map(key => key[0])
    expect(roots('inventory')).toEqual(expect.arrayContaining(['products', 'product']))
    expect(roots('invoices')).toEqual(expect.arrayContaining(['invoice', 'invoice-viewer', 'invoice-workspace-page']))
    expect(roots('orders')).toContain('dashboard')
  })

  it('names each key once, also after a reconnect, and always refreshes the job list', () => {
    const keys = realtimeQueryKeysFor(['orders', 'invoices', 'ORDERS'])
    expect(new Set(keys.map(key => key.join('/'))).size).toBe(keys.length)
    expect(keys.map(key => key[0])).toContain('jobs')
    expect(new Set(realtimeReconnectQueryKeys.map(key => key.join('/'))).size).toBe(realtimeReconnectQueryKeys.length)
    expect(realtimeQueryKeysFor([])).toEqual([])
  })
})
