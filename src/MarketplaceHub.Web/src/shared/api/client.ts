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

// A 401 means "signed out" only when the server says so. A wrong password or code in a confirmation (REAUTHENTICATION_FAILED,
// SECOND_FACTOR_FAILED) is a 401 too, but the session is still valid and the screen that asked shows the error.
const signedOutCodes = new Set(['AUTHENTICATION_REQUIRED', 'MFA_ATTEMPTS_EXCEEDED'])
export function isSignedOutProblem(status: number, code: string | undefined) {
  return status === 401 && (code === undefined || signedOutCodes.has(code))
}

// Irreversible work (data reset, connection deletion, credential change) needs a fresh proof of identity: the server answers
// 403 REAUTHENTICATION_REQUIRED. The app registers one handler that asks for the password (and code) and re-authenticates;
// when it succeeds, the refused request is sent once more, unchanged.
type StepUpHandler = () => Promise<boolean>
let stepUpHandler: StepUpHandler | null = null
export function setStepUpHandler(handler: StepUpHandler | null) {
  stepUpHandler = handler
  return () => { if (stepUpHandler === handler) stepUpHandler = null }
}

// Automatic polls that should not count as the person using the panel: the server can leave the session's idle clock alone.
export const backgroundPollHeaders = { 'X-Background-Poll': '1' } as const

// The last time the person pressed a key, clicked, touched or scrolled. A poll made right after (opening a page, the first
// fetch of a list) counts as use; a poll on a timer while nobody touches the panel does not, so an open tab still reaches the
// idle sign-out.
const userActiveWindowMs = 60_000
let lastUserActivityAt = 0
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
async function csrf(forceRefresh = false) {
  if (forceRefresh) csrfToken = null
  if (csrfToken) return csrfToken
  const response = await fetch('/api/v1/auth/csrf', { credentials: 'same-origin', cache: 'no-store' })
  if (!response.ok) throw new Error('Güvenlik anahtarı alınamadı.')
  csrfToken = (await response.json() as { token: string }).token
  return csrfToken
}

async function fetchWithCsrf(url: string, init?: RequestInit, retried = false): Promise<Response> {
  const method = init?.method?.toUpperCase() ?? 'GET'
  const headers = new Headers(init?.headers)
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) headers.set('X-CSRF-TOKEN', await csrf(retried))
  if (init?.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  const response = await fetch(url, { ...init, headers, credentials: 'same-origin' })
  if (response.status === 400 && !retried && !['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    const problem = await response.clone().json().catch(() => ({})) as ApiProblem
    if (problem.code === 'REQUEST_VERIFICATION_FAILED' || problem.type?.endsWith('/request-verification')) {
      csrfToken = null
      return fetchWithCsrf(url, init, true)
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

export async function hubApi<T>(path: string, init?: RequestInit, steppedUp = false): Promise<T> {
  const response = await fetchWithCsrf(`/api/v1${path}`, init)
  if (!response.ok) {
    const problem = await response.json().catch(() => ({})) as ApiProblem
    if (isSignedOutProblem(response.status, problem.code)) onUnauthorized()
    if (response.status === 403 && problem.code === 'REAUTHENTICATION_REQUIRED' && !steppedUp && stepUpHandler && await stepUpHandler()) return hubApi<T>(path, init, true)
    throw new ApiRequestError(problem.title ?? problem.code ?? `İşlem tamamlanamadı (${response.status}).`, response.status, problem.code, problem.fieldErrors, problem.tenants)
  }
  if (response.status === 204) return undefined as T
  return readSuccessJson<T>(response)
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
