import { render, screen } from '@testing-library/react'
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
