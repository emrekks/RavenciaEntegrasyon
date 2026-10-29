import type { QueryClient } from '@tanstack/react-query'

type ConnectionLike = { id?: unknown; platformCode?: unknown; displayName?: unknown }

function identityOf(value: unknown, connectionId: string) {
  if (!value || typeof value !== 'object') return null
  const connection = value as ConnectionLike
  if (connection.id !== connectionId || typeof connection.platformCode !== 'string') return null
  return { platformCode: connection.platformCode, displayName: typeof connection.displayName === 'string' ? connection.displayName : undefined }
}

// The store a notification names, read from the connections the panel has already loaded (any ['connections', …] list
// or a ['connection', id] detail). Nothing is fetched: a connection not in the cache leaves the store name out.
export function connectionIdentityFromCache(client: QueryClient, connectionId: string) {
  // A list that knows the store name wins over one that knows only the marketplace (the job filter's list).
  let partial: ReturnType<typeof identityOf> = null
  const detail = identityOf(client.getQueryData(['connection', connectionId]), connectionId)
  if (detail?.displayName) return detail
  partial = detail
  for (const [, data] of client.getQueriesData<unknown>({ queryKey: ['connections'] })) {
    const items = Array.isArray(data) ? data : data && typeof data === 'object' && Array.isArray((data as { items?: unknown }).items) ? (data as { items: unknown[] }).items : []
    for (const item of items) {
      const identity = identityOf(item, connectionId)
      if (identity?.displayName) return identity
      partial ??= identity
    }
  }
  return partial
}
