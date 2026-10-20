import type { QueryClient, QueryKey } from '@tanstack/react-query'
import { hubApi } from './api'

// Automatic polls must not keep an idle session alive (W1): the server leaves the session's idle clock alone for a
// request that carries this header. A query counts as a background poll when it already has data (so this is a
// refetch, not the first load) and the person has not clicked or typed in the last few seconds (so it is not their
// Yenile click or a page change).
export const backgroundPollHeaders = { 'X-Background-Poll': '1' } as const
const recentInteractionMs = 3000
let lastInteractionAt = 0
let tracking = false

function trackInteraction() {
  if (tracking || typeof window === 'undefined') return
  tracking = true
  const mark = () => { lastInteractionAt = Date.now() }
  window.addEventListener('pointerdown', mark, { capture: true, passive: true })
  window.addEventListener('keydown', mark, { capture: true, passive: true })
}

/** Test hook: when the person last interacted with the page. */
export function markUserInteraction(at = Date.now()) {
  lastInteractionAt = at
}

type PollContext = { client: QueryClient; queryKey: QueryKey }

export function isBackgroundPoll(context: PollContext, now = Date.now()) {
  trackInteraction()
  const loadedBefore = (context.client.getQueryState(context.queryKey)?.dataUpdatedAt ?? 0) > 0
  return loadedBefore && now - lastInteractionAt > recentInteractionMs
}

/** The init of a polled GET: unchanged for a first load or a user action, with the background-poll header otherwise. */
export function pollRequestInit(context: PollContext, init?: RequestInit, now = Date.now()): RequestInit | undefined {
  if (!isBackgroundPoll(context, now)) return init
  const headers = new Headers(init?.headers)
  Object.entries(backgroundPollHeaders).forEach(([name, value]) => headers.set(name, value))
  return { ...init, headers }
}

/** `hubApi` for a query with a refetch interval: pass the query function's context. */
export function pollHubApi<T>(path: string, context: PollContext, init?: RequestInit) {
  return hubApi<T>(path, pollRequestInit(context, init))
}
