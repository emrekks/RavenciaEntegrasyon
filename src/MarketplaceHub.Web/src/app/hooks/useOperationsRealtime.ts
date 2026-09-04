import { useEffect } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

export function useOperationsRealtime(enabled: boolean) {
  const client = useQueryClient()
  useEffect(() => {
    if (!enabled) return
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/operations')
      .withAutomaticReconnect([0, 1000, 3000, 10_000])
      .configureLogging(LogLevel.Warning)
      .build()
    const seenEvents = new Set<string>()
    const pendingResources = new Set<string>()
    let flushTimer: number | null = null
    const flush = () => {
      flushTimer = null
      const resources = [...pendingResources]
      pendingResources.clear()
      const queryKeysByResource: Record<string, string[][]> = {
        orders: [['orders'], ['dashboard-bootstrap'], ['dashboard-revenue-series']],
        returns: [['returns'], ['dashboard-bootstrap']],
        products: [['products'], ['dashboard-bootstrap']],
        inventory: [['inventory'], ['dashboard-bootstrap']],
        invoices: [['invoices'], ['dashboard-bootstrap']],
        connections: [['connections'], ['dashboard-bootstrap']],
        jobs: [['jobs']]
      }
      for (const resource of resources) {
        for (const queryKey of queryKeysByResource[resource.toLowerCase()] ?? []) void client.invalidateQueries({ queryKey })
      }
    }
    connection.on('operationsChanged', ({ resources, events }: { resources?: string[]; events?: { eventId?: string }[] }) => {
      for (const event of events ?? []) {
        if (!event.eventId || seenEvents.has(event.eventId)) continue
        seenEvents.add(event.eventId)
      }
      for (const resource of resources ?? []) pendingResources.add(resource)
      if (flushTimer === null) flushTimer = window.setTimeout(flush, 250)
    })
    connection.onreconnected(() => {
      void client.invalidateQueries({ queryKey: ['dashboard-bootstrap'] })
      void client.invalidateQueries({ queryKey: ['dashboard-revenue-series'] })
    })
    let stopped = false
    let retryTimer: number | null = null
    const start = async () => {
      if (stopped || connection.state !== 'Disconnected') return
      try {
        await connection.start()
      } catch {
        if (!stopped) retryTimer = window.setTimeout(() => void start(), 3000)
      }
    }
    void start()
    return () => {
      stopped = true
      if (retryTimer !== null) window.clearTimeout(retryTimer)
      if (flushTimer !== null) window.clearTimeout(flushTimer)
      void connection.stop()
    }
  }, [client, enabled])
}
