export type SessionState = 'PASSWORD_CHANGE_REQUIRED' | 'MFA_CHALLENGE' | 'ACTIVE' | 'REVOKED'
export type Me = { id: string; email: string; displayName: string; role: string | null; state: SessionState; tenantId: string | null }
export type TenantOption = { id: string; displayName: string }
import { readSuccessJson } from './read-success-json'

let csrfToken: string | null = null

// The session ended while the panel was open (idle timeout, revoked elsewhere, server restart). The app registers one
// handler that swaps the workspace for the sign-in screen; every request that hears 401 reports it once.
type UnauthorizedHandler = () => void
let unauthorizedHandler: UnauthorizedHandler | null = null
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null) {
  unauthorizedHandler = handler
  return () => { if (unauthorizedHandler === handler) unauthorizedHandler = null }
}
export function onUnauthorized() {
  csrfToken = null
  unauthorizedHandler?.()
}

// Client errors that a repeat of the same request cannot fix: signed out, not allowed, gone, stale version, invalid.
const permanentStatuses = new Set([401, 403, 404, 412, 422])
export function isPermanentRequestError(error: unknown) {
  return error instanceof ApiRequestError && permanentStatuses.has(error.status)
}

// A 401 means "signed out" only when the server says so. A wrong password or code in an authenticator change
// (REAUTHENTICATION_FAILED, SECOND_FACTOR_FAILED) is a 401 too, but the session is still valid and the screen that asked
// shows the error.
const signedOutCodes = new Set(['AUTHENTICATION_REQUIRED', 'MFA_ATTEMPTS_EXCEEDED'])
export function isSignedOutProblem(status: number, code: string | undefined) {
  return status === 401 && (code === undefined || signedOutCodes.has(code))
}

// Automatic polls that should not count as the person using the panel: the server can leave the session's idle clock alone.
export const backgroundPollHeaders = { 'X-Background-Poll': '1' } as const

// The last time the person pressed a key, clicked, touched or scrolled. A poll made right after (opening a page, the first
// fetch of a list) counts as use; a poll on a timer while nobody touches the panel does not, so an open tab still reaches the
// idle sign-out.
const userActiveWindowMs = 60_000
// Opening (or reloading) the panel is the person using it.
let lastUserActivityAt = Date.now()
export function noteUserActivity(at = Date.now()) { lastUserActivityAt = at }
export function isUserRecentlyActive(now = Date.now()) { return now - lastUserActivityAt < userActiveWindowMs }
export function trackUserActivity(target: Pick<Window, 'addEventListener' | 'removeEventListener'> = window) {
  const note = () => noteUserActivity()
  const events = ['pointerdown', 'keydown', 'wheel', 'touchstart'] as const
  events.forEach(name => target.addEventListener(name, note, { capture: true, passive: true }))
  return () => events.forEach(name => target.removeEventListener(name, note, { capture: true }))
}

/** Request options for a query that refetches on an interval: marked as a background poll unless the person was just active. */
export function backgroundPollInit(init?: RequestInit): RequestInit | undefined {
  if (isUserRecentlyActive()) return init
  const headers = new Headers(init?.headers)
  Object.entries(backgroundPollHeaders).forEach(([name, value]) => headers.set(name, value))
  return { ...init, headers }
}

/** hubApi for queries with a refetchInterval (see backgroundPollInit). Every polling query should read through this. */
export function hubPoll<T>(path: string, init?: RequestInit): Promise<T> {
  return hubApi<T>(path, backgroundPollInit(init))
}
// Every fetch of a token sets a new cookie, so several requests renewing at once (a product import sends one per
// store) would each overwrite the cookie the others' tokens match. One renewal is shared, and a request refused with
// a token that was already replaced takes the new one instead of renewing again.
let csrfRenewal: Promise<string> | null = null
async function csrf(refusedToken?: string) {
  if (csrfToken && csrfToken !== refusedToken) return csrfToken
  csrfRenewal ??= (async () => {
    const response = await fetch('/api/v1/auth/csrf', { credentials: 'same-origin', cache: 'no-store' })
    if (!response.ok) throw new Error('Güvenlik anahtarı alınamadı.')
    csrfToken = (await response.json() as { token: string }).token
    return csrfToken
  })().finally(() => { csrfRenewal = null })
  return csrfRenewal
}

async function fetchWithCsrf(url: string, init?: RequestInit, refusedToken?: string): Promise<Response> {
  const method = init?.method?.toUpperCase() ?? 'GET'
  const mutating = !['GET', 'HEAD', 'OPTIONS'].includes(method)
  const headers = new Headers(init?.headers)
  const token = mutating ? await csrf(refusedToken) : undefined
  if (token) headers.set('X-CSRF-TOKEN', token)
  if (init?.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  const response = await fetch(url, { ...init, headers, credentials: 'same-origin' })
  if (response.status === 400 && refusedToken === undefined && mutating) {
    const problem = await response.clone().json().catch(() => ({})) as ApiProblem
    if (problem.code === 'REQUEST_VERIFICATION_FAILED' || problem.type?.endsWith('/request-verification')) {
      return fetchWithCsrf(url, init, token ?? '')
    }
  }
  return response
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetchWithCsrf(`/api/v1/auth${path}`, init)
  if (!response.ok) {
    if (response.status === 401) csrfToken = null
    const problem = await response.json().catch(() => ({})) as ApiProblem
    const message = response.status === 401
      ? 'Oturum açılamadı.'
      : response.status === 429
        ? 'Çok fazla istek gönderildi. Kısa süre sonra yeniden deneyin.'
        : problem.title ?? problem.code ?? `İşlem tamamlanamadı (${response.status}).`
    throw new ApiRequestError(message, response.status, problem.code, problem.fieldErrors, problem.tenants)
  }
  if (response.status === 204) return undefined as T
  return readSuccessJson<T>(response)
}

export type ApiProblem = { type?: string; title?: string; code?: string; fieldErrors?: Record<string, string[]>; tenants?: TenantOption[] }

export class ApiRequestError extends Error {
  constructor(message: string, public readonly status: number, public readonly code?: string, public readonly fieldErrors?: Record<string, string[]>, public readonly tenants?: TenantOption[]) {
    super(message)
    this.name = 'ApiRequestError'
  }
}

/**
 * A read made while nobody has touched the panel for a minute was not asked for by the person: a refetch interval, a
 * realtime invalidation (useOperationsRealtime) or a refetch on focus. Every hubApi GET is marked as a background poll
 * then, whichever helper made it, so no forgotten timer can keep an idle session alive (W1, O6). Writes never are.
 */
export function automaticReadInit(init?: RequestInit): RequestInit | undefined {
  const method = init?.method?.toUpperCase() ?? 'GET'
  return method === 'GET' || method === 'HEAD' ? backgroundPollInit(init) : init
}

// A new version replaces the API in a few seconds; a request that lands in that gap gets 502/503/504 from the proxy (or
// no answer at all) and was never handled. A write carrying an Idempotency-Key is safe to send again, so it is, after
// 2, 4 and 6 seconds; the server answers a repeat of a request it did handle with the first result.
const restartRetryDelaysMs = [2000, 4000, 6000]
const restartStatuses = new Set([502, 503, 504])
async function fetchThroughRestart(url: string, init?: RequestInit): Promise<Response> {
  const repeatable = new Headers(init?.headers).has('Idempotency-Key')
  for (let attempt = 0; ; attempt++) {
    const last = !repeatable || attempt >= restartRetryDelaysMs.length
    try {
      const response = await fetchWithCsrf(url, init)
      if (last || !restartStatuses.has(response.status)) return response
    } catch (error) {
      if (last || !(error instanceof TypeError)) throw error
    }
    await new Promise(resolve => setTimeout(resolve, restartRetryDelaysMs[attempt]))
  }
}

export async function hubApi<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetchThroughRestart(`/api/v1${path}`, automaticReadInit(init))
  if (!response.ok) {
    const problem = await response.json().catch(() => ({})) as ApiProblem
    if (isSignedOutProblem(response.status, problem.code)) onUnauthorized()
    throw new ApiRequestError(problem.title ?? problem.code ?? `İşlem tamamlanamadı (${response.status}).`, response.status, problem.code, problem.fieldErrors, problem.tenants)
  }
  if (response.status === 204) return undefined as T
  return readSuccessJson<T>(response)
}

// A file the server writes (an Excel export), refused the way hubApi refuses: its bytes and the name it gives.
export async function hubFile(path: string, init?: RequestInit): Promise<{ blob: Blob; fileName: string | null; headers: Headers }> {
  const response = await fetchWithCsrf(`/api/v1${path}`, automaticReadInit(init))
  if (!response.ok) {
    const problem = await response.json().catch(() => ({})) as ApiProblem
    if (isSignedOutProblem(response.status, problem.code)) onUnauthorized()
    throw new ApiRequestError(problem.title ?? problem.code ?? `İşlem tamamlanamadı (${response.status}).`, response.status, problem.code, problem.fieldErrors, problem.tenants)
  }
  const disposition = response.headers.get('Content-Disposition') ?? ''
  const name = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1] ?? /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? null
  return { blob: await response.blob(), fileName: name ? decodeURIComponent(name) : null, headers: response.headers }
}

// Settings answer with the strong ETag "v{version}" and the same version in their envelope; a save sends it back so a
// change made meanwhile in another tab or session is refused (412) instead of overwritten. Version 0 = never saved.
export function settingsIfMatch(version: number | null | undefined): Record<string, string> {
  return version && version > 0 ? { 'If-Match': `"v${version}"` } : {}
}

export type CursorPage<T> = { items: T[]; nextCursor: string | null; hasMore: boolean; totalCount?: number | null }

/** Reads every cursor page. The server still keeps each individual request bounded. */
export async function loadAllPages<T>(path: string, limit = 200): Promise<CursorPage<T>> {
  const items: T[] = []
  const seenCursors = new Set<string>()
  let after: string | null = null

  while (true) {
    const [basePath, queryString = ''] = path.split('?')
    const params = new URLSearchParams(queryString)
    params.set('limit', String(limit))
    if (after) params.set('after', after)
    else params.delete('after')

    const page = await hubApi<CursorPage<T>>(`${basePath}?${params.toString()}`)
    items.push(...page.items)
    if (!page.hasMore) return { items, nextCursor: null, hasMore: false }
    if (!page.nextCursor || seenCursors.has(page.nextCursor)) throw new Error('Listenin devam sayfası güvenli biçimde alınamadı.')
    seenCursors.add(page.nextCursor)
    after = page.nextCursor
  }
}
