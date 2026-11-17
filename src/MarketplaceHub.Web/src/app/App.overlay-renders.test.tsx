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
// The open page counts its renders: opening a layer of the shell must not draw it again.
const pageRenders = vi.hoisted(() => ({ count: 0 }))
vi.mock('../features/dashboard/DashboardPage', () => ({ DashboardPage: () => { pageRenders.count += 1; return <h1>Genel bakış ekranı</h1> } }))

import { App } from './App'

const active = { id: 'u1', email: 'ayse@ravencia.com', displayName: 'Ayşe', role: 'OWNER', state: 'ACTIVE', tenantId: 't1' }
const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })

beforeEach(() => {
  pageRenders.count = 0
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    if (url === '/api/v1/auth/csrf') return json({ token: 'csrf' })
    if (url === '/api/v1/auth/me') return json(active)
    return json({ metrics: {}, items: [], totalCount: 0, dueSoonCount: 0 })
  }))
})
afterEach(() => { cleanup(); vi.unstubAllGlobals(); sessionStorage.clear(); localStorage.clear() })

async function openWorkspace() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const { host } = render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/dashboard']}><App /></MemoryRouter></QueryClientProvider>)
  await waitFor(() => host.querySelector('h1'))
  // Let the shell's own reads land first: only the opening itself is measured.
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 50)) })
  return host
}

describe('layers of the shell', () => {
  it('open the bell history, the mobile menu and Hızlı Arama without drawing the open page again', async () => {
    const host = await openWorkspace()
    const before = pageRenders.count
    act(() => host.querySelector<HTMLButtonElement>('.rv-topbar-notification-trigger')!.click())
    await waitFor(() => document.getElementById('rv-notification-history'))
    act(() => host.querySelector<HTMLButtonElement>('.rv-topbar-notification-trigger')!.click())
    act(() => host.querySelector<HTMLButtonElement>('button[aria-label="Ana menüyü aç"]')!.click())
    act(() => host.querySelector<HTMLButtonElement>('button[aria-label="Hızlı aramayı aç"]')!.click())
    await waitFor(() => document.querySelector('.rv-command-item'))
    expect(pageRenders.count).toBe(before)
  })
})
