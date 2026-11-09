import { afterEach, describe, expect, it, vi } from 'vitest'
import { RealtimeInvalidationThrottle, realtimeDataThrottleMs, realtimeInvalidationsFor, realtimeJobThrottleMs, realtimeQueryKeysByResource, realtimeQueryKeysFor, realtimeReconnectQueryKeys, skipsRealtimeRefetch } from './realtime-query-keys'

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
    expect(roots('orders')).toContain('dashboard-bootstrap')
    // The revenue panel reads a whole period; one order event must not refetch it.
    expect(roots('orders')).not.toContain('dashboard')
    // O19: the worker announces a rebuilt snapshot, so the menu badges re-read it instead of keeping the old counts.
    expect(roots('dashboard')).toContain('dashboard-bootstrap')
  })

  it('names each key once, also after a reconnect; a data change does not refresh the job list', () => {
    const keys = realtimeQueryKeysFor(['orders', 'invoices', 'ORDERS'])
    expect(new Set(keys.map(key => key.join('/'))).size).toBe(keys.length)
    expect(keys.map(key => key[0])).not.toContain('jobs')
    expect(new Set(realtimeReconnectQueryKeys.map(key => key.join('/'))).size).toBe(realtimeReconnectQueryKeys.length)
    expect(realtimeQueryKeysFor([])).toEqual([])
  })
})

describe('realtime refresh selection', () => {
  const plan = (resources: string[], events: Parameters<typeof realtimeInvalidationsFor>[1], watched: string[] = []) =>
    Object.fromEntries(realtimeInvalidationsFor(resources, events, id => watched.includes(id)).map(item => [item.queryKey.join('/'), item.minIntervalMs]))

  it('throttles data screens to once per 5 s and leaves the job screens alone', () => {
    const result = plan(['orders'], [{ resource: 'orders', aggregateType: 'Order', aggregateId: 'o1' }])
    expect(result['orders']).toBe(realtimeDataThrottleMs)
    expect(result['invoice-workspace-summary']).toBe(realtimeDataThrottleMs)
    expect(result['dashboard-bootstrap']).toBe(realtimeDataThrottleMs)
    expect(result['jobs']).toBeUndefined()
    expect(result['job']).toBeUndefined()
  })

  it('throttles routine job progress to once per 15 s', () => {
    const result = plan(['jobs'], [{ resource: 'jobs', aggregateType: 'IntegrationJob', aggregateId: 'j1', status: 'SUCCEEDED' }, { resource: 'jobs', aggregateType: 'IntegrationJob', aggregateId: 'j2', status: 'LEASED' }])
    expect(result).toEqual({ jobs: realtimeJobThrottleMs, job: realtimeJobThrottleMs })
  })

  it.each(['DEAD', 'MANUAL_REVIEW', 'BLOCKED', 'CANCELLED'])('refreshes the job screens at once for a job that turned %s', status => {
    const result = plan(['jobs'], [{ resource: 'jobs', aggregateType: 'IntegrationJob', aggregateId: 'j1', status }])
    expect(result['jobs']).toBe(0)
    expect(result['job/j1']).toBe(0)
  })

  it('refreshes at once for a job a person started or changed (announced under its domain) and for the job on screen', () => {
    expect(plan(['invoices'], [{ resource: 'invoices', aggregateType: 'IntegrationJob', aggregateId: 'j1', status: 'PENDING' }])['jobs']).toBe(0)
    const watched = plan(['jobs'], [{ resource: 'jobs', aggregateType: 'IntegrationJob', aggregateId: 'j1', status: 'LEASED' }], ['j1'])
    expect(watched['job/j1']).toBe(0)
    expect(watched['jobs']).toBe(realtimeJobThrottleMs)
  })

  it('still refreshes the job screens for an older server that sends no job events', () => {
    expect(plan(['jobs'], [])).toEqual({ jobs: realtimeJobThrottleMs, job: realtimeJobThrottleMs })
    expect(plan([], [])).toEqual({})
  })

  it('keeps a multi-page İşlem Takibi list for its own first-page refresh', () => {
    expect(skipsRealtimeRefetch(['jobs', 'page', 'x'], { pages: [{}, {}] })).toBe(true)
    expect(skipsRealtimeRefetch(['jobs', 'page', 'x'], { pages: [{}] })).toBe(false)
    expect(skipsRealtimeRefetch(['jobs', 'page-head', 'x'], { items: [] })).toBe(false)
    expect(skipsRealtimeRefetch(['orders'], { pages: [{}, {}] })).toBe(false)
  })
})

describe('realtime refresh throttle', () => {
  afterEach(() => { vi.useRealTimers() })

  it('runs the first refresh at once and the rest of an interval as one trailing refresh', () => {
    vi.useFakeTimers()
    const run = vi.fn()
    const throttle = new RealtimeInvalidationThrottle(run)
    for (let second = 0; second < 60; second++) {
      throttle.request({ queryKey: ['jobs'], minIntervalMs: realtimeJobThrottleMs })
      throttle.request({ queryKey: ['orders'], minIntervalMs: realtimeDataThrottleMs })
      vi.advanceTimersByTime(1000)
    }
    vi.advanceTimersByTime(20_000)
    const count = (root: string) => run.mock.calls.filter(([key]) => key[0] === root).length
    // 60 s of one change a second: about 4 job refreshes and 12 data refreshes instead of 60 each.
    expect(count('jobs')).toBeGreaterThanOrEqual(4)
    expect(count('jobs')).toBeLessThanOrEqual(5)
    expect(count('orders')).toBeGreaterThanOrEqual(12)
    expect(count('orders')).toBeLessThanOrEqual(13)
    throttle.dispose()
  })

  it('lets an urgent refresh through at once and drops the waiting one', () => {
    vi.useFakeTimers()
    const run = vi.fn()
    const throttle = new RealtimeInvalidationThrottle(run)
    throttle.request({ queryKey: ['jobs'], minIntervalMs: realtimeJobThrottleMs })
    throttle.request({ queryKey: ['jobs'], minIntervalMs: realtimeJobThrottleMs })
    expect(run).toHaveBeenCalledTimes(1)
    throttle.request({ queryKey: ['jobs'], minIntervalMs: 0 })
    expect(run).toHaveBeenCalledTimes(2)
    vi.advanceTimersByTime(realtimeJobThrottleMs)
    expect(run).toHaveBeenCalledTimes(2)
    throttle.dispose()
  })
})
