// TEMPORARY DIAGNOSTIC (remove after the standalone bottom-gap fix)
import { act } from 'react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../test/render'
import { FullScreenMenu } from './BottomDock'
import { DockDiagnosticHost } from './DockDiagnosticHost'
import { collectSnapshot, createTapCounter, formatSideBySide } from './dockDiagnosticFormat'

afterEach(() => { cleanup(); vi.useRealTimers() })

describe('dock diagnostic', () => {
  it('counts five taps within three seconds, and not slower ones', () => {
    const tap = createTapCounter()
    expect([0, 500, 1000, 1500].map(tap)).toEqual([false, false, false, false])
    expect(tap(2000)).toBe(true)
    const slow = createTapCounter()
    expect([0, 1000, 2000, 3500, 4000, 4500].map(slow)).toEqual([false, false, false, false, false, false])
  })

  it('formats open and closed side by side under a menu label', () => {
    const text = formatSideBySide({ menu: 'open', values: { a: '1', b: '2' } }, { menu: 'closed', values: { a: '3' } }, 'open')
    expect(text.split('\n')).toEqual(['menu=open   (acik | kapali)', 'a: 1 | 3', 'b: 2 | -'])
  })

  it('collects the measurements without throwing', () => {
    const snap = collectSnapshot(document, window)
    expect(snap.menu).toBe('closed')
    for (const key of ['inner w/h', 'vv w/h', '100svh', 'safe T/R/B/L', 'rect dock t/b/h', 'dock-layer.ancestor', 'gapBelowDock vs inner/screen/vv']) expect(snap.values[key]).toBeDefined()
  })

  it('opens the panel only after five taps on the profile card', async () => {
    render(<MemoryRouter><DockDiagnosticHost /><FullScreenMenu groups={[]} profile={{ name: 'ilker' }} onLogout={() => undefined} onClose={() => undefined} onNavigate={() => undefined} pathname="/" /></MemoryRouter>)
    const card = document.querySelector('.rv-fullmenu-profile') as HTMLElement
    for (let i = 0; i < 4; i += 1) act(() => card.click())
    expect(document.querySelector('[data-rv-diagnostic]')).toBeNull()
    act(() => card.click())
    await vi.waitFor(() => expect(document.querySelector('[data-rv-diagnostic]')).not.toBeNull())
    expect(document.body.textContent).toContain('Kopyala')
    expect(document.body.textContent).toContain('menu=open')
  })
})
