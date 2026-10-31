import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../test/render'
import { StickyActionBar } from './StickyActionBar'

afterEach(() => { cleanup(); vi.useRealTimers() })

describe('StickyActionBar', () => {
  it('is a labelled group while open and draws nothing while closed', () => {
    const { host, rerender } = render(<StickyActionBar open={false} label="Toplu işlemler">0 seçildi</StickyActionBar>)
    expect(host.querySelector('.rv-action-bar')).toBeNull()
    rerender(<StickyActionBar open label="Toplu işlemler" className="rr-bulk">2 seçildi</StickyActionBar>)
    const bar = host.querySelector('.rv-action-bar.rr-bulk')
    expect(bar?.getAttribute('role')).toBe('group')
    expect(bar?.getAttribute('aria-label')).toBe('Toplu işlemler')
    expect(bar?.textContent).toBe('2 seçildi')
  })

  it('slides out showing what it showed last, then leaves the page', () => {
    vi.useFakeTimers()
    const { host, rerender } = render(<StickyActionBar open label="Toplu işlemler">2 seçildi</StickyActionBar>)
    rerender(<StickyActionBar open={false} label="Toplu işlemler">0 seçildi</StickyActionBar>)
    const leaving = host.querySelector('.rv-action-bar.is-leaving')
    expect(leaving?.textContent).toBe('2 seçildi')
    expect(leaving?.hasAttribute('inert')).toBe(true)
    act(() => { vi.advanceTimersByTime(200) })
    expect(host.querySelector('.rv-action-bar')).toBeNull()
  })
})
