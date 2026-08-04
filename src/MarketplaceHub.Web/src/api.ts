export type SessionState = 'PASSWORD_CHANGE_REQUIRED' | 'MFA_CHALLENGE' | 'ACTIVE' | 'REVOKED'
export type Me = { id: string; email: string; displayName: string; role: string | null; state: SessionState; tenantId: string | null }

let csrfToken: string | null = null
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
    throw new Error(response.status === 401 ? 'Oturum açılamadı.' : 'İşlem tamamlanamadı.')
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export type ApiProblem = { type?: string; title?: string; code?: string; fieldErrors?: Record<string, string[]> }
export async function hubApi<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetchWithCsrf(`/api/v1${path}`, init)
  if (!response.ok) {
    if (response.status === 401) csrfToken = null
    const problem = await response.json().catch(() => ({})) as ApiProblem
    throw new Error(problem.title ?? problem.code ?? `İşlem tamamlanamadı (${response.status}).`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}
