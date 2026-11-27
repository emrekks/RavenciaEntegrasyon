import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, flush, render } from '../../test/render'

const hub = vi.hoisted(() => ({ connections: [] as Array<{ start: ReturnType<typeof vi.fn>; stop: ReturnType<typeof vi.fn>; state: string }>, startResult: null as null | (() => Promise<void>) }))

vi.mock('@microsoft/signalr', () => {
  class HttpError extends Error { constructor(message: string, public statusCode: number) { super(message) } }
  class HubConnectionBuilder {
    withUrl() { return this }
    withAutomaticReconnect() { return this }
    configureLogging() { return this }
    build() {
      const connection = { state: 'Disconnected', start: vi.fn(() => hub.startResult?.() ?? Promise.resolve()), stop: vi.fn(async () => {}), on: vi.fn(), onreconnected: vi.fn(), onclose: vi.fn() }
      hub.connections.push(connection)
      return connection
    }
  }
  return { HttpError, HubConnectionBuilder, LogLevel: { Warning: 3 } }
})

import { HttpError } from '@microsoft/signalr'
import { setUnauthorizedHandler } from '../../shared/api/client'
import { newOrderNoticeDelayMs, realtimeRetryDelayMs, useOperationsRealtime } from './useOperationsRealtime'
import { realtimeReconnectQueryKeys } from '../realtime-query-keys'

function Probe({ enabled }: { enabled: boolean }) { useOperationsRealtime(enabled); return null }
const view = (enabled: boolean, client = new QueryClient()) => <QueryClientProvider client={client}><Probe enabled={enabled} /></QueryClientProvider>

beforeEach(() => { hub.connections = []; hub.startResult = null; vi.useFakeTimers() })
afterEach(() => { cleanup(); vi.useRealTimers() })

describe('operations realtime connection', () => {
  it('waits longer after each failed start and settles at one minute', () => {
    expect([0, 1, 2, 3, 4, 8].map(realtimeRetryDelayMs)).toEqual([3000, 6000, 12_000, 24_000, 48_000, 60_000])
  })

  it('tries again later when the server cannot be reached', async () => {
    hub.startResult = () => Promise.reject(new Error('Failed to fetch'))
    render(view(true))
    await flush()
    expect(hub.connections[0].start).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(3000)
    expect(hub.connections[0].start).toHaveBeenCalledTimes(2)
  })

  it('stops and reports the ended session when the hub answers 401', async () => {
    const unauthorized = vi.fn()
    const release = setUnauthorizedHandler(unauthorized)
    hub.startResult = () => Promise.reject(new HttpError('Unauthorized', 401))
    render(view(true))
    await flush()
    await vi.advanceTimersByTimeAsync(120_000)
    expect(hub.connections[0].start).toHaveBeenCalledTimes(1)
    expect(unauthorized).toHaveBeenCalledTimes(1)
    release()
  })

  it('looks for the bell’s new-order notice again a moment after an order change', async () => {
    const client = new QueryClient()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    render(view(true, client))
    await flush()
    const connection = hub.connections[0] as unknown as { on: ReturnType<typeof vi.fn> }
    const handler = connection.on.mock.calls.find(([name]) => name === 'operationsChanged')![1] as (message: unknown) => void
    const noticeRefreshes = () => invalidate.mock.calls.filter(([filters]) => (filters?.queryKey as unknown[] | undefined)?.[0] === 'new-order-notices').length
    handler({ resources: ['orders'], events: [{ eventId: 'o1', resource: 'orders', aggregateType: 'Order', aggregateId: 'o1' }] })
    await vi.advanceTimersByTimeAsync(300)
    const atOnce = noticeRefreshes()
    await vi.advanceTimersByTimeAsync(newOrderNoticeDelayMs)
    expect(noticeRefreshes()).toBe(atOnce + 1)
    // A product change does not look for it.
    handler({ resources: ['products'], events: [{ eventId: 'p1', resource: 'products' }] })
    await vi.advanceTimersByTimeAsync(newOrderNoticeDelayMs + 6000)
    expect(noticeRefreshes()).toBe(atOnce + 1)
  })

  it('loads the SignalR client after rendering and throttles routine job refreshes', async () => {
    const client = new QueryClient()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    render(view(true, client))
    await flush()
    const connection = hub.connections[0] as unknown as { on: ReturnType<typeof vi.fn> }
    const handler = connection.on.mock.calls.find(([name]) => name === 'operationsChanged')![1] as (message: unknown) => void
    const jobRefreshes = () => invalidate.mock.calls.filter(([filters]) => (filters?.queryKey as unknown[] | undefined)?.[0] === 'jobs').length
    let id = 0
    const routine = () => handler({ resources: ['jobs'], events: [{ eventId: `e${id++}`, resource: 'jobs', aggregateType: 'IntegrationJob', aggregateId: 'j1', status: 'LEASED' }] })
    routine()
    await vi.advanceTimersByTimeAsync(300)
    expect(jobRefreshes()).toBe(1)
    for (let step = 0; step < 10; step++) { routine(); await vi.advanceTimersByTimeAsync(1000) }
    expect(jobRefreshes()).toBe(1)
    await vi.advanceTimersByTimeAsync(5000)
    expect(jobRefreshes()).toBe(2)
    // A failed job is shown at once.
    handler({ resources: ['jobs'], events: [{ eventId: 'dead', resource: 'jobs', aggregateType: 'IntegrationJob', aggregateId: 'j2', status: 'DEAD' }] })
    await vi.advanceTimersByTimeAsync(300)
    expect(jobRefreshes()).toBe(3)
  })

  it('refreshes every realtime root after it starts again following a closed connection, but not on the first start', async () => {
    const client = new QueryClient()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    render(view(true, client))
    await flush()
    expect(invalidate).not.toHaveBeenCalled()
    const connection = hub.connections[0] as unknown as { onclose: ReturnType<typeof vi.fn>; start: ReturnType<typeof vi.fn> }
    ;(connection.onclose.mock.calls[0][0] as () => void)()
    await vi.advanceTimersByTimeAsync(3000)
    expect(connection.start).toHaveBeenCalledTimes(2)
    const refreshed = new Set(invalidate.mock.calls.map(([filters]) => (filters?.queryKey as string[]).join('/')))
    for (const key of realtimeReconnectQueryKeys) expect(refreshed).toContain(key.join('/'))
  })

  // WEB-ALTYAPI-2 step 3: back on a visible tab with the connection down, it tries at once instead of waiting out the
  // backoff, and refreshes the lists once it is up; a visible tab with a live connection does nothing.
  it('tries at once when the tab becomes visible with the connection down', async () => {
    const client = new QueryClient()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    hub.startResult = () => Promise.reject(new Error('Failed to fetch'))
    render(view(true, client))
    await flush()
    // Several failures: the next try is 24 s away.
    for (const wait of [3000, 6000, 12_000]) await vi.advanceTimersByTimeAsync(wait)
    const connection = hub.connections[0]
    expect(connection.start).toHaveBeenCalledTimes(4)
    hub.startResult = null
    // The tab comes back. (No connection ever stood here, so its first start refreshes nothing; the refresh after a
    // gap is the test above.)
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' })
    document.dispatchEvent(new Event('visibilitychange'))
    await flush()
    expect(connection.start).toHaveBeenCalledTimes(5)
    // The scheduled try was cancelled: nothing more after the old backoff.
    await vi.advanceTimersByTimeAsync(30_000)
    expect(connection.start).toHaveBeenCalledTimes(5)
    // Up: a hidden-then-visible tab with the connection up does not start again.
    connection.state = 'Connected'
    document.dispatchEvent(new Event('visibilitychange'))
    await flush()
    expect(connection.start).toHaveBeenCalledTimes(5)
    expect(invalidate).not.toHaveBeenCalled()
  })

  it('connects again when it is switched off and on (signing in after the session ended)', async () => {
    const client = new QueryClient()
    const { rerender } = render(view(true, client))
    await flush()
    rerender(view(false, client))
    expect(hub.connections[0].stop).toHaveBeenCalled()
    rerender(view(true, client))
    await flush()
    expect(hub.connections).toHaveLength(2)
    expect(hub.connections[1].start).toHaveBeenCalledTimes(1)
  })
})
