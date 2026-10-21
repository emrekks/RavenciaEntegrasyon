import { dehydrate, hydrate, type Query, type QueryClient } from '@tanstack/react-query'
import { currentStorageScope, storageScopeOf } from './storage-scope'

// A reload (F5) used to start from nothing: the sign-in check, then every list and card asked the server again while
// the page showed "hazırlanıyor". The answers already on screen are kept in this tab's sessionStorage (gone when the
// tab closes, cleared at sign-out) and shown at once after a reload; every one of them is refetched straight away.
//
// The session itself (`['me']`) is never kept: the cookie is shared by every tab, so another tab may have signed out
// and into another tenant or account since this snapshot was written. The workspace waits for this page load's own
// /me; when it names another tenant or user than the snapshot was written for, every restored answer is dropped
// before anything of it is shown.
export const querySnapshotKey = 'rv-query-snapshot'
const maxAgeMs = 15 * 60_000
// One answer larger than this (a whole catalogue read) is not kept; the whole snapshot stays well under the quota.
const maxQueryChars = 400_000
const maxSnapshotChars = 2_500_000

// Set at sign-out: the page is leaving, and nothing of this session may be written again on its way out.
let stopped = false
// The tenant and user the restored answers were written for, until this page load's /me confirms or rejects them.
let restoredScope: string | null = null
// The tenant and user the latest /me (or sign-in) named; nothing is written before one has.
let confirmedScope: string | null = null

type Snapshot = { build: string; scope: string; savedAt: number; state: ReturnType<typeof dehydrate> }

const isSession = (query: Pick<Query, 'queryKey'>) => query.queryKey[0] === 'me'

// The running build: a deploy changes the main script's name, and an answer shaped for the old build is not reused.
function currentBuild() {
  if (typeof document === 'undefined') return 'test'
  return document.querySelector<HTMLScriptElement>('script[type="module"][src*="/assets/"]')?.getAttribute('src') ?? 'dev'
}

function storage(): Storage | null {
  try { return typeof sessionStorage === 'undefined' ? null : sessionStorage } catch { return null }
}

/** Puts the answers of this tab's last view back into the cache, marked stale so every one is asked for again. */
export function restoreQuerySnapshot(client: QueryClient, now = Date.now()) {
  const store = storage()
  if (!store) return
  try {
    const text = store.getItem(querySnapshotKey)
    if (!text) return
    const snapshot = JSON.parse(text) as Snapshot
    if (snapshot.build !== currentBuild() || now - snapshot.savedAt > maxAgeMs) { removeSnapshot(); return }
    if (snapshot.scope === 'anonymous') return
    // A snapshot of an earlier version may still hold the session; it is asked for again instead.
    hydrate(client, { ...snapshot.state, queries: snapshot.state.queries.filter(query => !isSession(query)) })
    restoredScope = snapshot.scope
    void client.invalidateQueries({ refetchType: 'none' })
  } catch {
    removeSnapshot()
  }
}

/**
 * Called with every /me answer (and at sign-in) before it reaches the cache. When the tenant or user differs from the
 * one the restored answers or the previous /me belonged to, every answer but the session is dropped with the snapshot.
 */
export function confirmQuerySnapshotScope(client: QueryClient, tenantId: string | null | undefined, userId: string | null | undefined) {
  const scope = storageScopeOf(tenantId, userId)
  const previous = confirmedScope ?? restoredScope
  restoredScope = null
  confirmedScope = scope
  if (previous === null || previous === scope) return
  client.removeQueries({ predicate: query => !isSession(query) })
  removeSnapshot()
}

/** A new sign-in starts from nothing: what was kept for the previous session (maybe another person) is dropped. */
export function forgetPreviousSession(client: QueryClient) {
  stopped = false
  restoredScope = null
  confirmedScope = null
  client.removeQueries({ predicate: query => !isSession(query) })
  removeSnapshot()
}

export function saveQuerySnapshot(client: QueryClient, now = Date.now()) {
  const store = storage()
  const scope = currentStorageScope()
  if (!store || stopped || scope === 'anonymous' || scope !== confirmedScope) return
  const state = dehydrate(client, { shouldDehydrateQuery: query => !isSession(query) && query.state.status === 'success' && query.state.data !== undefined })
  const queries = state.queries.filter(query => { try { return JSON.stringify(query.state.data).length <= maxQueryChars } catch { return false } })
  const text = JSON.stringify({ build: currentBuild(), scope, savedAt: now, state: { ...state, mutations: [], queries } } satisfies Snapshot)
  try {
    if (text.length > maxSnapshotChars) { store.removeItem(querySnapshotKey); return }
    store.setItem(querySnapshotKey, text)
  } catch {
    try { store.removeItem(querySnapshotKey) } catch { /* a blocked store keeps nothing */ }
  }
}

/** Keeps the snapshot current: written at most once a second after an answer arrives, and when the page is left. */
export function persistQuerySnapshot(client: QueryClient) {
  let timer: number | null = null
  const flush = () => { timer = null; saveQuerySnapshot(client) }
  const unsubscribe = client.getQueryCache().subscribe(event => {
    if (event.type !== 'updated' || event.action.type !== 'success' || timer !== null) return
    timer = window.setTimeout(flush, 1000)
  })
  const onHide = () => { if (timer !== null) window.clearTimeout(timer); flush() }
  window.addEventListener('pagehide', onHide)
  return () => { unsubscribe(); window.removeEventListener('pagehide', onHide); if (timer !== null) window.clearTimeout(timer) }
}

function removeSnapshot() {
  try { storage()?.removeItem(querySnapshotKey) } catch { /* nothing kept */ }
}

/** At sign-out, or when the session turns out to be gone: the snapshot is removed and not written again until the next sign-in. */
export function clearQuerySnapshot() {
  stopped = true
  restoredScope = null
  confirmedScope = null
  removeSnapshot()
}
