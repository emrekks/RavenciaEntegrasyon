import { act } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, waitFor } from '../test/render'

vi.mock('@microsoft/signalr', () => {
  class HttpError extends Error { constructor(message: string, public statusCode: number) { super(message) } }
  class HubConnectionBuilder {
    withUrl() { return this }
    withAutomaticReconnect() { return this }
    configureLogging() { return this }
    build() { return { state: 'Disconnected', start: async () => {}, stop: async () => {}, on() {}, onreconnected() {}, onclose() {} } }
  }
  return { HttpError, HubConnectionBuilder, LogLevel: { Warning: 3 } }
})
vi.mock('../features/dashboard/DashboardPage', () => ({ DashboardPage: () => <h1>Genel bakış ekranı</h1> }))

import { App } from './App'
import { confirmQuerySnapshotScope, forgetPreviousSession, querySnapshotKey, restoreQuerySnapshot, saveQuerySnapshot } from '../shared/query-snapshot'
import { setStorageScope } from '../shared/storage-scope'

type Reply = { status: number; body?: unknown }
const server = vi.hoisted(() => ({ me: [] as Reply[], calls: [] as Array<{ url: string; method: string; body?: string }>, hub: (_url: string): Reply => ({ status: 200, body: {} }) }))
const active = { id: 'u1', email: 'ayse@ravencia.com', displayName: 'Ayşe', role: 'OWNER', state: 'ACTIVE', tenantId: 't1' }

function reply({ status, body }: Reply) {
  return new Response(status === 204 ? null : JSON.stringify(body ?? {}), { status, headers: { 'Content-Type': 'application/json' } })
}

beforeEach(() => {
  server.me = []
  server.calls = []
  server.hub = () => ({ status: 200, body: { metrics: {}, items: [], totalCount: 0, dueSoonCount: 0 } })
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    server.calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : undefined })
    if (url === '/api/v1/auth/csrf') return reply({ status: 200, body: { token: 'csrf' } })
    if (url === '/api/v1/auth/me') return reply(server.me.length > 1 ? server.me.shift()! : server.me[0])
    if (url.startsWith('/api/v1/auth/')) return reply({ status: 200, body: { state: 'ACTIVE' } })
    return reply(server.hub(url))
  }))
})
afterEach(() => { cleanup(); vi.unstubAllGlobals(); sessionStorage.clear() })

function mount(path = '/dashboard', prepare?: (client: QueryClient) => void) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  prepare?.(client)
  return { ...render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}><App /></MemoryRouter></QueryClientProvider>), client }
}

function submit(form: HTMLFormElement) {
  act(() => { form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })) })
}

const shell = (host: HTMLElement) => host.querySelector('aside[aria-label="Ravencia ana menüsü"]')

describe('signing in', () => {
  it('asks for the password when there is no session', async () => {
    server.me = [{ status: 401 }]
    const { host } = mount()
    await waitFor(() => host.querySelector('#login-email'))
  })

  it('does not ask for the password while the server is unreachable or restarting', async () => {
    server.me = [{ status: 502 }]
    const { host } = mount()
    await waitFor(() => host.textContent?.includes('Veriler alınamadı'))
    expect(host.querySelector('#login-email')).toBeNull()
  })

  it('opens the workspace after a successful sign-in', async () => {
    server.me = [{ status: 401 }, { status: 200, body: active }]
    const { host } = mount('/orders')
    const password = await waitFor(() => host.querySelector<HTMLInputElement>('#login-password'))
    password.value = 'gizli-parola'
    submit(host.querySelector<HTMLFormElement>('form.rv-auth-form')!)
    await waitFor(() => shell(host))
    expect(server.calls.some(call => call.url === '/api/v1/auth/login' && call.method === 'POST')).toBe(true)
  })

  it('asks for the second step and opens the workspace once the code is accepted', async () => {
    server.me = [{ status: 200, body: { ...active, state: 'MFA_CHALLENGE' } }, { status: 200, body: active }]
    const { host } = mount()
    const code = await waitFor(() => host.querySelector<HTMLInputElement>('#mfa-code'))
    code.value = '123456'
    submit(host.querySelector<HTMLFormElement>('form.rv-auth-mfa-form')!)
    await waitFor(() => shell(host))
    const challenge = server.calls.find(call => call.url === '/api/v1/auth/mfa/challenge')
    expect(JSON.parse(challenge?.body ?? '{}')).toEqual({ code: '123456', recoveryCode: null })
  })

  it('lists the workspaces only after the code and sends the chosen one without a code (G8)', async () => {
    server.me = [{ status: 200, body: { ...active, state: 'MFA_CHALLENGE', tenantId: null } }, { status: 200, body: active }]
    const { host } = mount()
    await waitFor(() => host.querySelector('#mfa-code'))
    const fetchMock = vi.mocked(fetch)
    const original = fetchMock.getMockImplementation()!
    let challenges = 0
    fetchMock.mockImplementation(async (url, init) => {
      if (String(url) !== '/api/v1/auth/mfa/challenge') return original(url, init)
      server.calls.push({ url: String(url), method: 'POST', body: String(init?.body) })
      return ++challenges === 1
        ? reply({ status: 409, body: { code: 'TENANT_SELECTION_REQUIRED', tenants: [{ id: 't1', displayName: 'Birinci' }, { id: 't2', displayName: 'İkinci' }] } })
        : reply({ status: 200, body: { state: 'ACTIVE' } })
    })
    host.querySelector<HTMLInputElement>('#mfa-code')!.value = '123456'
    submit(host.querySelector<HTMLFormElement>('form.rv-auth-mfa-form')!)
    const select = await waitFor(() => host.querySelector<HTMLSelectElement>('#mfa-tenant'))
    expect(host.querySelector('#mfa-code')).toBeNull()
    expect(select.textContent).toContain('İkinci')
    act(() => { select.value = 't2'; select.dispatchEvent(new Event('change', { bubbles: true })) })
    submit(host.querySelector<HTMLFormElement>('form.rv-auth-mfa-form')!)
    await waitFor(() => shell(host))
    const sent = server.calls.filter(call => call.url === '/api/v1/auth/mfa/challenge').map(call => JSON.parse(call.body ?? '{}'))
    expect(sent).toEqual([{ code: '123456', recoveryCode: null }, { code: null, recoveryCode: null, tenantId: 't2' }])
  })

  it('tells that a wrong code was refused and stays on the second step', async () => {
    server.me = [{ status: 200, body: { ...active, state: 'MFA_CHALLENGE' } }]
    const { host } = mount()
    await waitFor(() => host.querySelector('#mfa-code'))
    const fetchMock = vi.mocked(fetch)
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url, init) => String(url) === '/api/v1/auth/mfa/challenge' ? reply({ status: 400, body: { code: 'MFA_INVALID' } }) : original(url, init))
    host.querySelector<HTMLInputElement>('#mfa-code')!.value = '000000'
    submit(host.querySelector<HTMLFormElement>('form.rv-auth-mfa-form')!)
    await waitFor(() => host.querySelector('[role="alert"]')?.textContent?.includes('Doğrulama kodu geçersiz'))
    expect(host.querySelector('#mfa-code')).not.toBeNull()
  })
})

describe('the forced password change (W30)', () => {
  it('asks for the new password twice and does not send a mismatch', async () => {
    server.me = [{ status: 200, body: { ...active, state: 'PASSWORD_CHANGE_REQUIRED' } }]
    const { host } = mount()
    await waitFor(() => host.querySelector('#confirm-password'))
    host.querySelector<HTMLInputElement>('#current-password')!.value = 'eski-parola'
    host.querySelector<HTMLInputElement>('#new-password')!.value = 'Mavi-Bulut 47 Kalem Defter!'
    host.querySelector<HTMLInputElement>('#confirm-password')!.value = 'Mavi-Bulut 47 Kalem Defte!'
    submit(host.querySelector<HTMLFormElement>('form.rv-auth-form')!)
    await waitFor(() => host.querySelector('[role="alert"]')?.textContent?.includes('aynı değil'))
    expect(server.calls.some(call => call.url === '/api/v1/auth/change-password')).toBe(false)
  })

  it('shows why the server refused the new password', async () => {
    server.me = [{ status: 200, body: { ...active, state: 'PASSWORD_CHANGE_REQUIRED' } }]
    const { host } = mount()
    await waitFor(() => host.querySelector('#confirm-password'))
    const fetchMock = vi.mocked(fetch)
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url, init) => String(url) === '/api/v1/auth/change-password' ? reply({ status: 400, body: { code: 'PASSWORD_POLICY_FAILED', title: 'Yeni parola yaygın bir parola olmamalıdır.' } }) : original(url, init))
    host.querySelector<HTMLInputElement>('#current-password')!.value = 'eski-parola'
    host.querySelector<HTMLInputElement>('#new-password')!.value = 'MarketplaceHub1!'
    host.querySelector<HTMLInputElement>('#confirm-password')!.value = 'MarketplaceHub1!'
    submit(host.querySelector<HTMLFormElement>('form.rv-auth-form')!)
    await waitFor(() => host.querySelector('[role="alert"]')?.textContent?.includes('yaygın bir parola olmamalıdır'))
  })
})

describe('irreversible work needs no re-authentication', () => {
  it('sends the request once and never opens a password window', async () => {
    server.me = [{ status: 200, body: active }]
    const { host } = mount()
    await waitFor(() => shell(host))
    let resets = 0
    server.hub = url => {
      if (url !== '/api/v1/settings/data-reset') return { status: 200, body: { metrics: {}, items: [], totalCount: 0, dueSoonCount: 0 } }
      resets++
      return { status: 200, body: { products: 0 } }
    }
    const { hubApi } = await import('../shared/api')
    let result: unknown
    await act(async () => { result = await hubApi('/settings/data-reset', { method: 'POST', body: '{}' }) })
    expect(result).toEqual({ products: 0 })
    expect(resets).toBe(1)
    expect(document.querySelector('input[type="password"]')).toBeNull()
    expect(server.calls.some(call => call.url === '/api/v1/auth/reauthenticate')).toBe(false)
  })
})

describe('a session that ends while the panel is open', () => {
  it('swaps the workspace for the sign-in screen with a note instead of retrying forever', async () => {
    server.me = [{ status: 200, body: active }]
    const { host } = mount()
    await waitFor(() => shell(host))
    server.hub = () => ({ status: 401, body: { title: 'Oturum gerekli.' } })
    const { hubApi } = await import('../shared/api')
    await act(async () => { await hubApi('/orders/summary').catch(() => undefined) })
    await waitFor(() => host.querySelector('#login-email'))
    expect(shell(host)).toBeNull()
    expect(host.textContent).toContain('Oturumunuzun süresi doldu')
  })

  it('removes the reload snapshot and writes it no more until the next sign-in', async () => {
    server.me = [{ status: 200, body: active }]
    // A fresh page load: the module-wide stop left by the test before is lifted as a sign-in would.
    const { host, client } = mount('/dashboard', forgetPreviousSession)
    await waitFor(() => shell(host))
    client.setQueryData(['orders', 'summary'], { new: 4 })
    saveQuerySnapshot(client)
    expect(sessionStorage.getItem(querySnapshotKey)).not.toBeNull()
    server.hub = () => ({ status: 401, body: { title: 'Oturum gerekli.' } })
    const { hubApi } = await import('../shared/api')
    await act(async () => { await hubApi('/orders/summary').catch(() => undefined) })
    await waitFor(() => host.querySelector('#login-email'))
    expect(sessionStorage.getItem(querySnapshotKey)).toBeNull()
    client.setQueryData(['orders', 'summary'], { new: 5 })
    saveQuerySnapshot(client)
    expect(sessionStorage.getItem(querySnapshotKey)).toBeNull()
  })
})

describe('a reload after another tab signed into another tenant', () => {
  it('waits for /me and drops what the snapshot kept for the other tenant', async () => {
    const before = new QueryClient()
    forgetPreviousSession(before)
    confirmQuerySnapshotScope(before, 'tenant-x', 'u1')
    setStorageScope('tenant-x', 'u1')
    before.setQueryData(['me'], { ...active, tenantId: 'tenant-x', displayName: 'X kişisi' })
    before.setQueryData(['buyers', 'b1'], { name: 'X alıcısı' })
    saveQuerySnapshot(before)

    server.me = [{ status: 200, body: active }]
    const { host, client } = mount('/dashboard', restoreQuerySnapshot)
    expect(client.getQueryData(['me'])).toBeUndefined()
    expect(shell(host)).toBeNull()
    await waitFor(() => shell(host))
    expect(host.textContent).toContain('Ayşe')
    expect(host.textContent).not.toContain('X kişisi')
    expect(client.getQueryData(['buyers', 'b1'])).toBeUndefined()
    expect(sessionStorage.getItem(querySnapshotKey)).toBeNull()
  })
})
