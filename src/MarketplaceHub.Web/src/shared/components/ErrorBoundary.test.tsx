import { afterEach, describe, expect, it, vi } from 'vitest'
import { byText, cleanup, click, render } from '../../test/render'
import { ErrorBoundary } from './ErrorBoundary'
import { formatMoney } from '../format-money'

const state = vi.hoisted(() => ({ broken: true }))
function Screen({ currency }: { currency: string }) {
  if (state.broken) throw new RangeError(`Invalid currency code : ${currency}`)
  return <p>Tutar hazır</p>
}

afterEach(() => { cleanup(); state.broken = true; vi.restoreAllMocks() })

describe('screen error boundary', () => {
  it('shows a box in place of a screen that throws, and the rest stays', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const { host } = render(<div><nav>Menü</nav><ErrorBoundary resetKey="/orders"><Screen currency="XX" /></ErrorBoundary></div>)
    expect(host.querySelector('nav')?.textContent).toBe('Menü')
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('Bu ekran açılamadı')
  })

  it('tries the screen again on request', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const { host } = render(<ErrorBoundary><Screen currency="XX" /></ErrorBoundary>)
    state.broken = false
    click(byText(host, 'button', 'Tekrar Dene'))
    expect(host.textContent).toContain('Tutar hazır')
  })

  it('clears the error when the person goes to another page', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const { host, rerender } = render(<ErrorBoundary resetKey="/orders"><Screen currency="XX" /></ErrorBoundary>)
    state.broken = false
    rerender(<ErrorBoundary resetKey="/returns"><Screen currency="XX" /></ErrorBoundary>)
    expect(host.textContent).toContain('Tutar hazır')
  })

  it('is not needed for an unknown currency any more', () => {
    expect(formatMoney(10, 'XX')).toBe('10,00 XX')
  })
})
