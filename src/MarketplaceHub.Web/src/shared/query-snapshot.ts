import { dehydrate, hydrate, type QueryClient } from '@tanstack/react-query'
import { currentStorageScope } from './storage-scope'

// A reload (F5) used to start from nothing: the sign-in check, then every list and card asked the server again while
// the page showed "hazırlanıyor". The answers already on screen are kept in this tab's sessionStorage (gone when the
// tab closes, cleared at sign-out) and shown at once after a reload; every one of them is refetched straight away.
export const querySnapshotKey = 'rv-query-snapshot'
const maxAgeMs = 15 * 60_000
// One answer larger than this (a whole catalogue read) is not kept; the whole snapshot stays well under the quota.
const maxQueryChars = 400_000
const maxSnapshotChars = 2_500_000

// Set at sign-out: the page is leaving, and nothing of this session may be written again on its way out.
let stopped = false

type Snapshot = { build: string; scope: string; savedAt: number; state: ReturnType<typeof dehydrate> }

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
    hydrate(client, snapshot.state)
    void client.invalidateQueries({ refetchType: 'none' })
  } catch {
    removeSnapshot()
  }
}

/** A new sign-in starts from nothing: what was kept for the previous session (maybe another person) is dropped. */
export function forgetPreviousSession(client: QueryClient) {
  stopped = false
  client.removeQueries({ predicate: query => query.queryKey[0] !== 'me' })
  removeSnapshot()
}

export function saveQuerySnapshot(client: QueryClient, now = Date.now()) {
  const store = storage()
  if (!store || stopped || currentStorageScope() === 'anonymous') return
  const state = dehydrate(client, { shouldDehydrateQuery: query => query.state.status === 'success' && query.state.data !== undefined })
  const queries = state.queries.filter(query => { try { return JSON.stringify(query.state.data).length <= maxQueryChars } catch { return false } })
  const text = JSON.stringify({ build: currentBuild(), scope: currentStorageScope(), savedAt: now, state: { ...state, mutations: [], queries } } satisfies Snapshot)
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
  removeSnapshot()
}
