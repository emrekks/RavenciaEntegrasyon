import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, test, vi } from 'vitest'
import { App } from './App'

test('shows a safe loading state before auth resolution', () => {
  globalThis.fetch = () => new Promise(() => {})
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter><App /></MemoryRouter></QueryClientProvider>)
  expect(screen.getByRole('status')).toHaveTextContent('Güvenli oturum doğrulanıyor')
})

test('collapses the sidebar to icons and remembers the choice', async () => {
  localStorage.clear()
  globalThis.fetch = async input => {
    const url = String(input)
    if (url.endsWith('/api/v1/auth/me')) return new Response(JSON.stringify({ id: 'user-1', email: 'admin@ravencia.test', tenantId: 'tenant-1', state: 'ACTIVE', displayName: 'Ravencia Admin', role: 'OWNER' }), { status: 200, headers: { 'Content-Type': 'application/json' } })
    if (url.includes('/api/v1/orders?')) return new Response(JSON.stringify({ items: [], hasMore: false, nextCursor: null }), { status: 200, headers: { 'Content-Type': 'application/json' } })
    return new Response('{}', { status: 404, headers: { 'Content-Type': 'application/json' } })
  }
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/orders']}><App /></MemoryRouter></QueryClientProvider>)
  const toggle = await screen.findByRole('button', { name: 'Menüyü daralt' })
  fireEvent.click(toggle)
  expect(toggle.closest('.app-shell')).toHaveClass('sidebar-collapsed')
  expect(localStorage.getItem('ravencia.sidebarCollapsed')).toBe('1')
  expect(screen.getByRole('button', { name: 'Menüyü genişlet' })).toBeInTheDocument()
})

test('enables authenticator and revokes another session through the security APIs', async () => {
  const calls: string[] = []
  vi.spyOn(window, 'confirm').mockReturnValue(true)
  globalThis.fetch = vi.fn(async (input, init) => {
    const url = String(input); calls.push(`${init?.method ?? 'GET'} ${url}`)
    if (url.endsWith('/api/v1/auth/me')) return new Response(JSON.stringify({ id: 'user-1', email: 'admin@ravencia.test', tenantId: 'tenant-1', state: 'ACTIVE', displayName: 'Ravencia Admin', role: 'OWNER' }), { status: 200, headers: { 'Content-Type': 'application/json' } })
    if (url.endsWith('/api/v1/auth/security-status')) return new Response(JSON.stringify({ totpState: 'DISABLED', recoveryCodesRemaining: 0 }), { status: 200, headers: { 'Content-Type': 'application/json' } })
    if (url.endsWith('/api/v1/auth/sessions')) return new Response(JSON.stringify([
      { id: '11111111-1111-1111-1111-111111111111', state: 'ACTIVE', current: true, issuedAt: '2026-08-09T18:00:00Z', lastSeenAt: '2026-08-09T19:00:00Z', expiresAt: '2026-08-10T19:00:00Z' },
      { id: '22222222-2222-2222-2222-222222222222', state: 'ACTIVE', current: false, issuedAt: '2026-08-09T17:00:00Z', lastSeenAt: '2026-08-09T18:30:00Z', expiresAt: '2026-08-10T18:30:00Z' },
    ]), { status: 200, headers: { 'Content-Type': 'application/json' } })
    if (url.endsWith('/api/v1/auth/csrf')) return new Response(JSON.stringify({ token: 'csrf-security' }), { status: 200, headers: { 'Content-Type': 'application/json' } })
    if (url.endsWith('/api/v1/auth/reauthenticate')) return new Response(null, { status: 204 })
    if (url.endsWith('/api/v1/auth/mfa/setup')) return new Response(JSON.stringify({ otpauthUri: 'otpauth://totp/Ravencia', qrSvg: '<svg></svg>', expiresAt: '2026-08-09T20:00:00Z' }), { status: 200, headers: { 'Content-Type': 'application/json' } })
    if (url.endsWith('/api/v1/auth/mfa/confirm')) return new Response(JSON.stringify({ recoveryCodes: ['RECOVERY-1', 'RECOVERY-2'] }), { status: 200, headers: { 'Content-Type': 'application/json' } })
    if (url.includes('/api/v1/auth/sessions/22222222-2222-2222-2222-222222222222/revoke')) return new Response(null, { status: 204 })
    return new Response('{}', { status: 404, headers: { 'Content-Type': 'application/json' } })
  }) as typeof fetch

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/settings/security']}><App /></MemoryRouter></QueryClientProvider>)

  fireEvent.click(await screen.findByRole('button', { name: 'Authenticator’ı etkinleştir' }))
  fireEvent.change(screen.getByLabelText('Mevcut parola'), { target: { value: 'safe-password' } })
  fireEvent.click(screen.getByRole('button', { name: 'Devam et' }))
  expect(await screen.findByAltText('Authenticator QR kodu')).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('6 haneli doğrulama kodu'), { target: { value: '123456' } })
  fireEvent.click(screen.getByRole('button', { name: 'Etkinleştir' }))
  expect(await screen.findByText('RECOVERY-1')).toBeInTheDocument()

  fireEvent.click(screen.getByRole('button', { name: 'Oturumu sonlandır' }))
  await waitFor(() => expect(calls).toContain('POST /api/v1/auth/sessions/22222222-2222-2222-2222-222222222222/revoke'))
})
