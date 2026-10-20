import { useEffect } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { HttpError, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { RecentOperationEventIds } from '../operations-event-deduplication'
import { realtimeQueryKeysFor, realtimeReconnectQueryKeys } from '../realtime-query-keys'
import { onUnauthorized } from '../../shared/api/client'

// Seconds before the next connection try: 3, 6, 12, 24, then every 60.
export function realtimeRetryDelayMs(attempt: number) {
  return Math.min(60_000, 3000 * 2 ** Math.max(0, attempt))
}

function signedOut(error: unknown) {
  return error instanceof HttpError && (error.statusCode === 401 || error.statusCode === 403)
}

export function useOperationsRealtime(enabled: boolean) {
  const client = useQueryClient()
  useEffect(() => {
    if (!enabled) return
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/operations')
      .withAutomaticReconnect([0, 1000, 3000, 10_000])
      .configureLogging(LogLevel.Warning)
      .build()
    const seenEvents = new RecentOperationEventIds()
    const pendingResources = new Set<string>()
    let flushTimer: number | null = null
    const flush = () => {
      flushTimer = null
      const resources = [...pendingResources]
      pendingResources.clear()
      for (const queryKey of realtimeQueryKeysFor(resources)) void client.invalidateQueries({ queryKey })
    }
    connection.on('operationsChanged', ({ resources, events }: { resources?: string[]; events?: { eventId?: string }[] }) => {
      if (!seenEvents.shouldProcess((events ?? []).map(event => event.eventId))) return
      for (const resource of resources ?? []) pendingResources.add(resource)
      if (flushTimer === null) flushTimer = window.setTimeout(flush, 250)
    })
    connection.onreconnected(() => {
      for (const queryKey of realtimeReconnectQueryKeys) void client.invalidateQueries({ queryKey })
    })
    let stopped = false
    let failures = 0
    let retryTimer: number | null = null
    const scheduleStart = () => {
      if (stopped || retryTimer !== null) return
      retryTimer = window.setTimeout(() => { retryTimer = null; void start() }, realtimeRetryDelayMs(failures))
      failures += 1
    }
    const start = async () => {
      if (stopped || connection.state !== 'Disconnected') return
      try {
        await connection.start()
        failures = 0
      } catch (error) {
        // The session is gone: trying again cannot help; the app shows the sign-in screen and a new sign-in starts over.
        if (signedOut(error)) { stopped = true; onUnauthorized(); return }
        scheduleStart()
      }
    }
    connection.onclose(() => scheduleStart())
    void start()
    return () => {
      stopped = true
      if (retryTimer !== null) window.clearTimeout(retryTimer)
      if (flushTimer !== null) window.clearTimeout(flushTimer)
      void connection.stop()
    }
  }, [client, enabled])
}
