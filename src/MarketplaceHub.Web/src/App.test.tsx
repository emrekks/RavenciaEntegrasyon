import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, test } from 'vitest'
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
