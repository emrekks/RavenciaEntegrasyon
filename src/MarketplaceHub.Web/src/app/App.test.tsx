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
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

function mount(path = '/dashboard') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}><App /></MemoryRouter></QueryClientProvider>)
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

describe('irreversible work asks for the password again (A4)', () => {
  it('opens the prompt, re-authenticates and repeats the refused request', async () => {
    server.me = [{ status: 200, body: active }]
    const { host } = mount()
    await waitFor(() => shell(host))
    let resets = 0
    server.hub = url => {
      if (url !== '/api/v1/settings/data-reset') return { status: 200, body: { metrics: {}, items: [], totalCount: 0, dueSoonCount: 0 } }
      resets++
      return resets === 1 ? { status: 403, body: { code: 'REAUTHENTICATION_REQUIRED', title: 'Yeniden doğrulama gerekir.' } } : { status: 200, body: { products: 0 } }
    }
    const { hubApi } = await import('../shared/api')
    let result: unknown
    const call = hubApi('/settings/data-reset', { method: 'POST', body: '{}' }).then(value => { result = value })
    const password = await waitFor(() => document.querySelector<HTMLInputElement>('.rv-step-up-form input[name="password"]'))
    await waitFor(stepUpReady)
    password.value = 'gizli-parola'
    submit(document.querySelector<HTMLFormElement>('.rv-step-up-form')!)
    await act(async () => { await call })
    expect(result).toEqual({ products: 0 })
    expect(resets).toBe(2)
    expect(server.calls.some(call => call.url === '/api/v1/auth/reauthenticate' && JSON.parse(call.body ?? '{}').password === 'gizli-parola')).toBe(true)
    expect(document.querySelector('.rv-step-up')).toBeNull()
  })

  it('asks for the authenticator code when one is on and sends it with the password', async () => {
    server.me = [{ status: 200, body: active }]
    const { host } = mount()
    await waitFor(() => shell(host))
    const fetchMock = vi.mocked(fetch)
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url, init) => String(url) === '/api/v1/auth/security-status' ? reply({ status: 200, body: { totpState: 'ENABLED', recoveryCodesRemaining: 8 } }) : original(url, init))
    let deletes = 0
    server.hub = url => url === '/api/v1/connections/c1/deep-delete' ? (++deletes === 1 ? { status: 403, body: { code: 'REAUTHENTICATION_REQUIRED' } } : { status: 204 }) : { status: 200, body: { metrics: {}, items: [], totalCount: 0, dueSoonCount: 0 } }
    const { hubApi } = await import('../shared/api')
    const call = hubApi('/connections/c1/deep-delete', { method: 'POST', body: '{}' })
    const code = await waitFor(() => document.querySelector<HTMLInputElement>('.rv-step-up-form input[name="factor"]'))
    document.querySelector<HTMLInputElement>('.rv-step-up-form input[name="password"]')!.value = 'gizli-parola'
    code.value = '123456'
    submit(document.querySelector<HTMLFormElement>('.rv-step-up-form')!)
    await act(async () => { await call })
    expect(deletes).toBe(2)
    const sent = JSON.parse(server.calls.find(call => call.url === '/api/v1/auth/reauthenticate')?.body ?? '{}')
    expect(sent).toEqual({ password: 'gizli-parola', code: '123456', recoveryCode: null })
  })

  it('keeps the window open on a wrong password and gives the refusal back on cancel', async () => {
    server.me = [{ status: 200, body: active }]
    const { host } = mount()
    await waitFor(() => shell(host))
    const fetchMock = vi.mocked(fetch)
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url, init) => String(url) === '/api/v1/auth/reauthenticate' ? reply({ status: 401, body: { code: 'REAUTHENTICATION_FAILED', title: 'Parola doğrulanamadı.' } }) : original(url, init))
    server.hub = url => url === '/api/v1/settings/data-reset' ? { status: 403, body: { code: 'REAUTHENTICATION_REQUIRED', title: 'Yeniden doğrulama gerekir.' } } : { status: 200, body: { metrics: {}, items: [], totalCount: 0, dueSoonCount: 0 } }
    const { hubApi } = await import('../shared/api')
    let failure: unknown
    const call = hubApi('/settings/data-reset', { method: 'POST', body: '{}' }).catch(reason => { failure = reason })
    const password = await waitFor(() => document.querySelector<HTMLInputElement>('.rv-step-up-form input[name="password"]'))
    await waitFor(stepUpReady)
    password.value = 'yanlis'
    submit(document.querySelector<HTMLFormElement>('.rv-step-up-form')!)
    await waitFor(() => document.querySelector('.rv-step-up [role="alert"]')?.textContent?.includes('Parola doğrulanamadı'))
    expect(shell(host)).not.toBeNull()
    act(() => { Array.from(document.querySelectorAll<HTMLButtonElement>('.rv-step-up button')).find(button => button.textContent === 'Vazgeç')!.click() })
    await act(async () => { await call })
    expect(failure).toMatchObject({ status: 403, code: 'REAUTHENTICATION_REQUIRED' })
    expect(document.querySelector('.rv-step-up')).toBeNull()
  })
})

function stepUpReady() {
  const button = document.querySelector<HTMLButtonElement>('.rv-step-up button[type="submit"]')
  return button !== null && !button.disabled
}

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
})
