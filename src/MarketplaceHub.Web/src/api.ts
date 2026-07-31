export type SessionState = 'PASSWORD_CHANGE_REQUIRED' | 'MFA_CHALLENGE' | 'ACTIVE' | 'REVOKED'
export type Me = { id: string; email: string; displayName: string; state: SessionState; tenantId: string | null }

let csrfToken: string | null = null
async function csrf() {
  if (csrfToken) return csrfToken
  const response = await fetch('/api/v1/auth/csrf', { credentials: 'same-origin' })
  if (!response.ok) throw new Error('Güvenlik anahtarı alınamadı.')
  csrfToken = (await response.json() as { token: string }).token
  return csrfToken
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const method = init?.method?.toUpperCase() ?? 'GET'
  const headers = new Headers(init?.headers)
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) headers.set('X-CSRF-TOKEN', await csrf())
  if (init?.body) headers.set('Content-Type', 'application/json')
  const response = await fetch(`/api/v1/auth${path}`, { ...init, headers, credentials: 'same-origin' })
  if (!response.ok) { csrfToken = null; throw new Error(response.status === 401 ? 'Oturum açılamadı.' : 'İşlem tamamlanamadı.') }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}
