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
// While the Oturum Doldu window is open (D-7) the panel sends nothing to the API: no poll, no read and no write may reach
// it, since another tab may have signed this browser into another account meanwhile. Only the window's own sign-in
// requests (under /auth, through api()) go out; the hold is lifted once the same person is signed in again.
let sessionHold = false
export function setSessionHold(held: boolean) { sessionHold = held }
const sessionHeldError = () => new ApiRequestError('Oturum doldu; yeniden giriş yapın.', 401, 'AUTHENTICATION_REQUIRED')

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
// A 5xx the API wrote itself (application/problem+json, e.g. 502 "Claude görsel dosyası üretmedi") was handled: the
// server holds its key as an unknown outcome and a repeat only gets IDEMPOTENCY_RESULT_UNKNOWN, so it is returned as is.
const restartRetryDelaysMs = [2000, 4000, 6000]
const restartStatuses = new Set([502, 503, 504])
export function isProxyRestartResponse(response: Response) {
  return restartStatuses.has(response.status) && !(response.headers.get('Content-Type') ?? '').toLowerCase().startsWith('application/problem+json')
}
async function fetchThroughRestart(url: string, init?: RequestInit): Promise<Response> {
  const repeatable = new Headers(init?.headers).has('Idempotency-Key')
  for (let attempt = 0; ; attempt++) {
    const last = !repeatable || attempt >= restartRetryDelaysMs.length
    try {
      const response = await fetchWithCsrf(url, init)
      if (last || !isProxyRestartResponse(response)) return response
    } catch (error) {
      if (last || !(error instanceof TypeError)) throw error
    }
    await new Promise(resolve => setTimeout(resolve, restartRetryDelaysMs[attempt]))
  }
}

// 409 IDEMPOTENCY_IN_PROGRESS: the first attempt with the same key is still being handled. Not a failure; the person is
// told calmly that the result will show up, and the key is kept (shared/idempotency.ts isStillRunning).
export const stillRunningMessage = 'Bu işlem hâlâ sürüyor; birkaç saniye sonra sonucu listede göreceksiniz.'

/** hubApi's options: fetch's own, and how long the request may wait for its answer. */
export type HubRequestInit = RequestInit & {
  /** Give up after this many ms (default defaultReadTimeoutMs for a read, defaultWriteTimeoutMs for a write; 0: no limit). */
  timeoutMs?: number
}

// A request that gets no answer at all (a stuck proxy, a half-open connection) used to wait forever with the screen on
// "yükleniyor" or "Kaydediliyor…". Reads and writes give up after a generous limit, well above the slowest report read
// and the slowest inline write (WEB-ALTYAPI-11). A write that runs out of time is never sent again by the panel:
// stopping the wait does not stop the server, so the person is told the result is unknown and the lists are read again
// (setWriteTimeoutHandler); the idempotency rules in shared/idempotency.ts decide what a resend does (a timeout is not an
// HTTP answer, so a resend may reuse the key and replays the first answer).
export const defaultReadTimeoutMs = 90_000
export const defaultWriteTimeoutMs = 90_000
export const clientTimeoutCode = 'CLIENT_TIMEOUT'
export const clientTimeoutMessage = 'Sunucu zamanında yanıt vermedi. Bağlantınızı kontrol edip yeniden deneyin.'
export const clientWriteTimeoutMessage = 'Sunucu 90 saniye içinde yanıt vermedi; işlemin sonucu bilinmiyor. Liste yenileniyor; yeniden göndermeden önce sonucu kontrol edin.'

// The app registers what a timed-out write triggers: reading the lists again, so the screen shows what the server holds.
type WriteTimeoutHandler = () => void
let writeTimeoutHandler: WriteTimeoutHandler | null = null
export function setWriteTimeoutHandler(handler: WriteTimeoutHandler | null) {
  writeTimeoutHandler = handler
  return () => { if (writeTimeoutHandler === handler) writeTimeoutHandler = null }
}

// The signal a request goes out with: the caller's (TanStack Query's, so a query that left the screen is aborted) joined
// with the request's time limit. Built by hand rather than with AbortSignal.any/timeout, which older Safari lacks.
function requestDeadline(init: HubRequestInit | undefined) {
  const method = init?.method?.toUpperCase() ?? 'GET'
  const write = method !== 'GET' && method !== 'HEAD'
  const limit = init?.timeoutMs ?? (write ? defaultWriteTimeoutMs : defaultReadTimeoutMs)
  if (!(limit > 0)) return null
  const controller = new AbortController()
  const outer = init?.signal ?? null
  let timedOut = false
  const forward = () => controller.abort(outer?.reason)
  if (outer?.aborted) forward()
  else outer?.addEventListener('abort', forward, { once: true })
  const timer = setTimeout(() => { timedOut = true; controller.abort(new DOMException('İstek süresi doldu.', 'TimeoutError')) }, limit)
  return {
    signal: controller.signal,
    write,
    timedOut: () => timedOut,
    dispose: () => { clearTimeout(timer); outer?.removeEventListener('abort', forward) },
  }
}

export async function hubApi<T>(path: string, init?: HubRequestInit): Promise<T> {
  const deadline = requestDeadline(init)
  let requestInit: RequestInit | undefined = init
  if (init && 'timeoutMs' in init) { const { timeoutMs: _timeoutMs, ...rest } = init; requestInit = rest }
  if (deadline) requestInit = { ...requestInit, signal: deadline.signal }
  if (sessionHold) { deadline?.dispose(); throw sessionHeldError() }
  try {
    const response = await fetchThroughRestart(`/api/v1${path}`, automaticReadInit(requestInit))
    if (!response.ok) {
      const problem = await response.json().catch(() => ({})) as ApiProblem
      if (isSignedOutProblem(response.status, problem.code)) onUnauthorized()
      const message = response.status === 409 && problem.code === 'IDEMPOTENCY_IN_PROGRESS' ? stillRunningMessage : problem.title ?? problem.code ?? `İşlem tamamlanamadı (${response.status}).`
      throw new ApiRequestError(message, response.status, problem.code, problem.fieldErrors, problem.tenants)
    }
    if (response.status === 204) return undefined as T
    return await readSuccessJson<T>(response)
  } catch (error) {
    // Status 0: no HTTP answer came (failedWithHttpAnswer is false), so a resend may reuse the request's key.
    if (deadline?.timedOut()) {
      if (!deadline.write) throw new ApiRequestError(clientTimeoutMessage, 0, clientTimeoutCode)
      // A write's result is unknown: the lists are read again, nothing is sent again.
      writeTimeoutHandler?.()
      throw new ApiRequestError(clientWriteTimeoutMessage, 0, clientTimeoutCode)
    }
    throw error
  } finally {
    deadline?.dispose()
  }
}

// A file the server writes (an Excel export), refused the way hubApi refuses: its bytes and the name it gives.
export async function hubFile(path: string, init?: RequestInit): Promise<{ blob: Blob; fileName: string | null; headers: Headers }> {
  if (sessionHold) throw sessionHeldError()
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

/**
 * Reads every cursor page. The server still keeps each individual request bounded. The second argument is the page size,
 * or the page size and the query's signal (a query that leaves the screen stops reading further pages).
 */
export async function loadAllPages<T>(path: string, options: number | { limit?: number; signal?: AbortSignal } = {}): Promise<CursorPage<T>> {
  const { limit = 200, signal = undefined } = typeof options === 'number' ? { limit: options, signal: undefined } : options
  const items: T[] = []
  const seenCursors = new Set<string>()
  let after: string | null = null

  while (true) {
    const [basePath, queryString = ''] = path.split('?')
    const params = new URLSearchParams(queryString)
    params.set('limit', String(limit))
    if (after) params.set('after', after)
    else params.delete('after')

    const page = await hubApi<CursorPage<T>>(`${basePath}?${params.toString()}`, signal ? { signal } : undefined)
    items.push(...page.items)
    if (!page.hasMore) return { items, nextCursor: null, hasMore: false }
    if (!page.nextCursor || seenCursors.has(page.nextCursor)) throw new Error('Listenin devam sayfası güvenli biçimde alınamadı.')
    seenCursors.add(page.nextCursor)
    after = page.nextCursor
  }
}
