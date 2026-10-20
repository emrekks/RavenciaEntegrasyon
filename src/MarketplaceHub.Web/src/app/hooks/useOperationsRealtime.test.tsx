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
import { realtimeRetryDelayMs, useOperationsRealtime } from './useOperationsRealtime'

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
