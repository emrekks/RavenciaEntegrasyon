// What a realtime change announcement refreshes. The server names the resource that changed (AppDbContext.OutboxChange:
// orders, products, inventory, returns, invoices, connections; jobs from the job broadcaster); each one maps to the
// roots of the queries that show it. Every root here must be used by at least one query (realtime-query-keys.test.ts).
export const realtimeQueryKeysByResource: Record<string, readonly (readonly string[])[]> = {
  // Orders carry their packages, so shipments change with them; the invoice workspace lists orders waiting for invoices.
  // The dashboard's revenue panel is not refreshed per order event: it reads a whole period and refreshes itself every
  // minute while on screen; the operational cards (dashboard-bootstrap) are a cheap snapshot read.
  orders: [['orders'], ['shipments'], ['shipment'], ['invoice-workspace'], ['invoice-workspace-page'], ['invoice-workspace-summary'], ['dashboard-bootstrap']],
  returns: [['returns'], ['return'], ['dashboard-bootstrap']],
  products: [['products'], ['product'], ['publication-status'], ['dashboard-bootstrap']],
  // Stock lives on the product screens; there is no separate inventory query.
  inventory: [['products'], ['product'], ['dashboard-bootstrap']],
  // Invoice state is shown on the invoice screens and on each order row.
  invoices: [['invoice'], ['invoice-viewer'], ['invoice-workspace'], ['invoice-workspace-page'], ['invoice-workspace-summary'], ['orders'], ['dashboard-bootstrap']],
  connections: [['connections'], ['connection'], ['invoice-workspace-page'], ['invoice-workspace-summary'], ['dashboard-bootstrap'], ['dashboard']],
  questions: [['questions']],
  // The worker rebuilt the dashboard snapshot after a change was already announced: the menu badges and cards re-read it.
  dashboard: [['dashboard-bootstrap']],
  jobs: [['jobs'], ['job']],
}

// After the connection comes back, anything may have changed while it was away.
export const realtimeReconnectQueryKeys: readonly (readonly string[])[] = Array.from(
  new Map(Object.values(realtimeQueryKeysByResource).flat().map(key => [key.join('\u0000'), key])).values()
)

/** The query roots a batch of changed resources refreshes. */
export function realtimeQueryKeysFor(resources: readonly string[]) {
  const keys = new Map<string, readonly string[]>()
  const add = (key: readonly string[]) => keys.set(key.join('\u0000'), key)
  for (const resource of resources) for (const key of realtimeQueryKeysByResource[resource.toLowerCase()] ?? []) add(key)
  return [...keys.values()]
}

/** One change of an announcement (OperationsRealtimeBroadcaster): job events carry the job's new status. */
export type RealtimeEvent = { eventId?: string; resource?: string; operation?: string; aggregateType?: string; aggregateId?: string; status?: string | null }

/** One refresh to make: the query root, and how long after its last refresh another one may run (0: at once). */
export type RealtimeInvalidation = { queryKey: readonly string[]; minIntervalMs: number }

// A busy store announces many changes a minute (measured 9 Nov 2026: ~72 batches a minute, every one refetching the
// job list and the summaries). Data screens refresh at most once per 5 s (the first change of a quiet spell at once),
// the job screens at most once per 15 s, except for what needs the user's eye.
export const realtimeDataThrottleMs = 5_000
export const realtimeJobThrottleMs = 15_000

// A job that failed, waits for a person or was stopped: the attention badge and the job list show it at once.
const attentionJobStatuses: ReadonlySet<string> = new Set(['DEAD', 'MANUAL_REVIEW', 'BLOCKED', 'CANCELLED', 'FAILED'])

function isJobEvent(event: RealtimeEvent) {
  return event.aggregateType === 'IntegrationJob' || event.resource?.toLowerCase() === 'jobs'
}

/**
 * The refreshes one batch of announcements asks for. Data resources refresh their screens (throttled); job events refresh
 * the job screens: at once when a job needs attention, when a person started or changed it (the API announces those
 * under the job's own domain, the worker under "jobs"), or for the job a screen has open (isWatchedJob); otherwise at
 * most once per 15 s. A server without job statuses in its events (older build) still gets the throttled refresh.
 */
export function realtimeInvalidationsFor(resources: readonly string[], events: readonly RealtimeEvent[], isWatchedJob: (jobId: string) => boolean = () => false): RealtimeInvalidation[] {
  const out = new Map<string, RealtimeInvalidation>()
  const add = (queryKey: readonly string[], minIntervalMs: number) => {
    const id = queryKey.join('\u0000')
    const current = out.get(id)
    if (!current || minIntervalMs < current.minIntervalMs) out.set(id, { queryKey, minIntervalMs })
  }
  const dataResources = resources.filter(resource => resource.toLowerCase() !== 'jobs')
  for (const queryKey of realtimeQueryKeysFor(dataResources)) add(queryKey, realtimeDataThrottleMs)
  const jobEvents = events.filter(isJobEvent)
  // An older server may announce "jobs" without events; it is a job change all the same.
  if (!jobEvents.length && resources.some(resource => resource.toLowerCase() === 'jobs')) jobEvents.push({ resource: 'jobs' })
  for (const event of jobEvents) {
    const status = event.status?.toUpperCase() ?? ''
    const startedByPerson = event.aggregateType === 'IntegrationJob' && Boolean(event.resource) && event.resource!.toLowerCase() !== 'jobs'
    const urgent = attentionJobStatuses.has(status) || startedByPerson
    add(['jobs'], urgent ? 0 : realtimeJobThrottleMs)
    if (event.aggregateId && (urgent || isWatchedJob(event.aggregateId))) add(['job', event.aggregateId], 0)
    else add(['job'], realtimeJobThrottleMs)
  }
  return [...out.values()]
}

/**
 * Runs each refresh at most once per its interval: the first one at once, later ones together at the end of the interval
 * (one trailing run however many arrived). A refresh due at once also takes the place of a waiting one.
 */
export class RealtimeInvalidationThrottle {
  private readonly lastRun = new Map<string, number>()
  private readonly waiting = new Map<string, ReturnType<typeof setTimeout>>()

  constructor(private readonly run: (queryKey: readonly string[]) => void, private readonly now: () => number = () => Date.now()) {}

  request({ queryKey, minIntervalMs }: RealtimeInvalidation) {
    const id = queryKey.join('\u0000')
    const wait = minIntervalMs <= 0 ? 0 : (this.lastRun.get(id) ?? Number.NEGATIVE_INFINITY) + minIntervalMs - this.now()
    if (wait <= 0) {
      const timer = this.waiting.get(id)
      if (timer !== undefined) { clearTimeout(timer); this.waiting.delete(id) }
      this.fire(id, queryKey)
      return
    }
    if (this.waiting.has(id)) return
    this.waiting.set(id, setTimeout(() => { this.waiting.delete(id); this.fire(id, queryKey) }, wait))
  }

  dispose() {
    for (const timer of this.waiting.values()) clearTimeout(timer)
    this.waiting.clear()
  }

  private fire(id: string, queryKey: readonly string[]) {
    this.lastRun.set(id, this.now())
    this.run(queryKey)
  }
}

/**
 * İşlem Takibi's list with older pages loaded is not refetched page by page on a realtime refresh: its first page is
 * read on its own (['jobs', 'page-head', ...]) and put in front, which the same refresh covers.
 */
export function skipsRealtimeRefetch(queryKey: readonly unknown[], data: unknown) {
  if (queryKey[0] !== 'jobs' || queryKey[1] !== 'page') return false
  const pages = (data as { pages?: unknown[] } | undefined)?.pages
  return Array.isArray(pages) && pages.length > 1
}
