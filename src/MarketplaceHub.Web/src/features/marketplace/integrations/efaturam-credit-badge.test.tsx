import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const hubApi = vi.fn()
vi.mock('../../../shared/api', () => ({ hubApi: (...args: unknown[]) => hubApi(...args) }))

const { EFaturamCreditBadge, EFaturamCreditBadgeView, efaturamCreditValue } = await import('./efaturam-credit-badge')

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  hubApi.mockReset()
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

describe('efaturamCreditValue', () => {
  it('has a value only when the provider answered with one', () => {
    expect(efaturamCreditValue({ available: true, remainingCredit: 1240 })).toBe(1240)
    expect(efaturamCreditValue({ available: true, remainingCredit: 0 })).toBe(0)
    expect(efaturamCreditValue({ available: false, reason: 'user-is-not-partner' })).toBeNull()
    expect(efaturamCreditValue({ available: true, remainingCredit: null })).toBeNull()
    expect(efaturamCreditValue(undefined)).toBeNull()
  })
})

describe('EFaturamCreditBadgeView', () => {
  it('shows the formatted credit when available', () => {
    act(() => root.render(<EFaturamCreditBadgeView credit={{ available: true, remainingCredit: 1240 }} />))
    const badge = container.querySelector('.efaturam-credit-badge')
    expect(badge?.textContent).toBe('1.240 Kontör')
    expect(badge?.classList.contains('is-low')).toBe(false)
  })

  it('uses the warning look under 50', () => {
    act(() => root.render(<EFaturamCreditBadgeView credit={{ available: true, remainingCredit: 49 }} />))
    expect(container.querySelector('.efaturam-credit-badge.is-low')?.textContent).toBe('49 Kontör')
  })

  it('renders nothing when unavailable or still loading', () => {
    act(() => root.render(<EFaturamCreditBadgeView credit={{ available: false, reason: 'user-is-not-partner' }} />))
    expect(container.innerHTML).toBe('')
    act(() => root.render(<EFaturamCreditBadgeView credit={undefined} />))
    expect(container.innerHTML).toBe('')
  })
})

describe('EFaturamCreditBadge', () => {
  function renderBadge(enabled = true) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    act(() => root.render(<QueryClientProvider client={client}><EFaturamCreditBadge connectionId="c-1" enabled={enabled} /></QueryClientProvider>))
  }

  it('appears only once the answer is available, with no placeholder before', async () => {
    let resolve: (value: unknown) => void = () => {}
    hubApi.mockReturnValue(new Promise(done => { resolve = done }))
    renderBadge()
    expect(hubApi).toHaveBeenCalledWith('/connections/c-1/efaturam-remaining-credit', expect.anything())
    expect(container.innerHTML).toBe('')
    await act(async () => { resolve({ available: true, remainingCredit: 1240 }) })
    // The query settles on its own schedule (slower on CI): wait for the badge rather than a fixed tick.
    await vi.waitFor(async () => {
      await act(async () => { await new Promise(done => setTimeout(done, 10)) })
      expect(container.textContent).toBe('1.240 Kontör')
    }, { timeout: 3000 })
  })

  it('stays empty for a non-partner account and on errors', async () => {
    hubApi.mockResolvedValue({ available: false, reason: 'user-is-not-partner' })
    renderBadge()
    await act(async () => {})
    expect(container.innerHTML).toBe('')

    hubApi.mockRejectedValue(new Error('boom'))
    act(() => root.unmount())
    root = createRoot(container)
    renderBadge()
    await act(async () => {})
    expect(container.innerHTML).toBe('')
  })

  it('does not ask while the card hides its data', () => {
    renderBadge(false)
    expect(hubApi).not.toHaveBeenCalled()
    expect(container.innerHTML).toBe('')
  })
})
