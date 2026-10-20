// What a realtime change announcement refreshes. The server names the resource that changed (AppDbContext.OutboxChange:
// orders, products, inventory, returns, invoices, connections; jobs from the job broadcaster); each one maps to the
// roots of the queries that show it. Every root here must be used by at least one query (realtime-query-keys.test.ts).
export const realtimeQueryKeysByResource: Record<string, readonly (readonly string[])[]> = {
  // Orders carry their packages, so shipments change with them; the invoice workspace lists orders waiting for invoices.
  orders: [['orders'], ['shipments'], ['shipment'], ['invoice-workspace'], ['invoice-workspace-page'], ['invoice-workspace-summary'], ['dashboard-bootstrap'], ['dashboard']],
  returns: [['returns'], ['return'], ['dashboard-bootstrap']],
  products: [['products'], ['product'], ['publication-status'], ['dashboard-bootstrap']],
  // Stock lives on the product screens; there is no separate inventory query.
  inventory: [['products'], ['product'], ['dashboard-bootstrap']],
  // Invoice state is shown on the invoice screens and on each order row.
  invoices: [['invoice'], ['invoice-viewer'], ['invoice-workspace'], ['invoice-workspace-page'], ['invoice-workspace-summary'], ['orders'], ['dashboard-bootstrap']],
  connections: [['connections'], ['connection'], ['invoice-workspace-page'], ['invoice-workspace-summary'], ['dashboard-bootstrap'], ['dashboard']],
  questions: [['questions']],
  jobs: [['jobs'], ['job']],
}

// After the connection comes back, anything may have changed while it was away.
export const realtimeReconnectQueryKeys: readonly (readonly string[])[] = Array.from(
  new Map(Object.values(realtimeQueryKeysByResource).flat().map(key => [key.join('\u0000'), key])).values()
)

/** The query roots a batch of changed resources refreshes. Job events also refresh the job list. */
export function realtimeQueryKeysFor(resources: readonly string[]) {
  const keys = new Map<string, readonly string[]>()
  const add = (key: readonly string[]) => keys.set(key.join('\u0000'), key)
  for (const resource of resources) for (const key of realtimeQueryKeysByResource[resource.toLowerCase()] ?? []) add(key)
  // Job events are announced under the resource the job works on (orders, invoices...), so any change also refreshes the
  // job list; it only refetches while the list is on screen.
  if (resources.length) add(['jobs'])
  return [...keys.values()]
}
