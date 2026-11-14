import { useQuery } from '@tanstack/react-query'
import { loadAllPages } from '../../shared/api'
import { isActiveMarketplaceConnection, normalizeMarketplacePlatformCode } from './marketplace-platform-support'
import { activeReturnSyncConnections } from './return-sync'

// The data source of a summary card's main value (shared rule, shared/metric-value.ts): an active connection that feeds
// it. Without one the card's big number is a dash; with one a real zero reads "0".
type SourceConnection = { id: string; platformCode: string; status: string }
export type MetricSourceOptions = {
  /** orders (default): any active marketplace connection; returns: one that reads returns. */
  kind?: 'orders' | 'returns'
  /** Only these platforms feed the value (a report's platform filter); empty or missing means all. */
  platforms?: readonly string[] | null
  /** Only this one connection feeds the value ("ALL" or missing means all). */
  connectionId?: string | null
}

/** True when an active connection feeds the value, false when none does. */
export function hasMetricSource(connections: readonly SourceConnection[], { kind = 'orders', platforms, connectionId }: MetricSourceOptions = {}): boolean {
  const active = kind === 'returns' ? activeReturnSyncConnections([...connections]) : connections.filter(isActiveMarketplaceConnection)
  const codes = platforms?.length ? new Set(platforms.map(normalizeMarketplacePlatformCode)) : null
  return active.some(connection => (!codes || codes.has(normalizeMarketplacePlatformCode(connection.platformCode))) && (!connectionId || connectionId === 'ALL' || connection.id === connectionId))
}

/** One shared read of the connections for every card; undefined while it is not read (a card then waits, never blocks). */
export function useMetricSource(options: MetricSourceOptions = {}): boolean | undefined {
  const connections = useQuery({ queryKey: ['connections', 'metric-source'], queryFn: () => loadAllPages<SourceConnection>('/connections'), staleTime: 60_000 })
  return connections.data ? hasMetricSource(connections.data.items, options) : undefined
}
