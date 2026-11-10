import { useEffect } from 'react'
import { useQueryClient, type Query } from '@tanstack/react-query'
import type { HubConnection } from '@microsoft/signalr'
import { RecentOperationEventIds } from '../operations-event-deduplication'
import { RealtimeInvalidationThrottle, realtimeInvalidationsFor, realtimeReconnectQueryKeys, skipsRealtimeRefetch, type RealtimeEvent } from '../realtime-query-keys'
import { onUnauthorized } from '../../shared/api/client'

// Seconds before the next connection try: 3, 6, 12, 24, then every 60.
export function realtimeRetryDelayMs(attempt: number) {
  return Math.min(60_000, 3000 * 2 ** Math.max(0, attempt))
}

// The SignalR client (~84 KB) is not part of the first load: it is read after the first screen has rendered. A failed
// read (a deploy replaced the chunk, the network dropped) is tried again with the same backoff as a failed start.
const loadSignalR = () => import('@microsoft/signalr')

export function useOperationsRealtime(enabled: boolean) {
  const client = useQueryClient()
  useEffect(() => {
    if (!enabled) return
    let stopped = false
    let failures = 0
    let retryTimer: number | null = null
    let flushTimer: number | null = null
    let connection: HubConnection | null = null
    let signedOut: (error: unknown) => boolean = () => false
    const seenEvents = new RecentOperationEventIds()
    const pendingResources = new Set<string>()
    const pendingEvents: RealtimeEvent[] = []
    const refetchable = (query: Query) => !skipsRealtimeRefetch(query.queryKey, query.state.data)
    const throttle = new RealtimeInvalidationThrottle(queryKey => { void client.invalidateQueries({ queryKey, predicate: refetchable }) })
    // The job a screen has open (İşlem Takibi's detail) follows its own changes at once.
    const isWatchedJob = (jobId: string) => client.getQueryCache().findAll({ queryKey: ['job', jobId], exact: true, type: 'active' }).length > 0
    const flush = () => {
      flushTimer = null
      const resources = [...pendingResources]
      const events = pendingEvents.splice(0)
      pendingResources.clear()
      for (const invalidation of realtimeInvalidationsFor(resources, events, isWatchedJob)) throttle.request(invalidation)
    }
    const scheduleStart = () => {
      if (stopped || retryTimer !== null) return
      retryTimer = window.setTimeout(() => { retryTimer = null; void start() }, realtimeRetryDelayMs(failures))
      failures += 1
    }
    const build = async () => {
      const signalR = await loadSignalR()
      signedOut = error => error instanceof signalR.HttpError && (error.statusCode === 401 || error.statusCode === 403)
      const built = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/operations')
        .withAutomaticReconnect([0, 1000, 3000, 10_000])
        .configureLogging(signalR.LogLevel.Warning)
        .build()
      built.on('operationsChanged', ({ resources, events }: { resources?: string[]; events?: RealtimeEvent[] }) => {
        if (!seenEvents.shouldProcess((events ?? []).map(event => event.eventId))) return
        for (const resource of resources ?? []) pendingResources.add(resource)
        pendingEvents.push(...(events ?? []))
        if (flushTimer === null) flushTimer = window.setTimeout(flush, 250)
      })
      built.onreconnected(() => {
        for (const queryKey of realtimeReconnectQueryKeys) void client.invalidateQueries({ queryKey })
      })
      built.onclose(() => scheduleStart())
      return built
    }
    const start = async () => {
      if (stopped) return
      try {
        connection ??= await build()
        if (stopped || connection.state !== 'Disconnected') return
        await connection.start()
        failures = 0
      } catch (error) {
        // The session is gone: trying again cannot help; the app shows the sign-in screen and a new sign-in starts over.
        if (signedOut(error)) { stopped = true; onUnauthorized(); return }
        scheduleStart()
      }
    }
    void start()
    return () => {
      stopped = true
      if (retryTimer !== null) window.clearTimeout(retryTimer)
      if (flushTimer !== null) window.clearTimeout(flushTimer)
      throttle.dispose()
      void connection?.stop()
    }
  }, [client, enabled])
}
