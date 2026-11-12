import { useQueries } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import type { MarketplaceConnection } from './shared'

// PttAVM's "Manuel Dış Yazmalar" switch (the MANUAL_WRITE policy, Entegrasyonlar › mağaza › Pazaryerine Yazma) gates the
// sends a user starts there, besides the Dış Yazma Ana Anahtarı. Read from the same sync-policies list the integration
// page shows (same query key, so a switch turned there is seen here). A missing row reads as closed, as the server
// treats it (PttAvmManualWritePolicy); a list not read yet or not readable stays unknown (undefined), and the server decides.
export function usePttAvmManualWrites(connections: readonly MarketplaceConnection[]): ReadonlyMap<string, boolean> {
  const pttAvm = connections.filter(connection => connection.platformCode.trim().toUpperCase() === 'PTTAVM')
  const results = useQueries({
    queries: pttAvm.map(connection => ({
      queryKey: ['sync-policies', connection.id],
      queryFn: () => hubApi<Array<{ resourceType: string; enabled: boolean }>>(`/connections/${connection.id}/sync-policies`),
      staleTime: 30_000
    }))
  })
  const states = new Map<string, boolean>()
  pttAvm.forEach((connection, index) => {
    const policies = results[index]?.data
    if (Array.isArray(policies)) states.set(connection.id, policies.some(policy => policy.resourceType === 'MANUAL_WRITE' && policy.enabled))
  })
  return states
}
