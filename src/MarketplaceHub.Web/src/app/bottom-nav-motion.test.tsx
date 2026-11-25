import { act, useRef } from 'react'
import { createRoot } from 'react-dom/client'
import { describe, expect, it } from 'vitest'
import { useStuckHead } from '../shared/hooks/useStuckHead'

// node:fs without @types/node, as bottom-nav.test.tsx does.
const fs = (globalThis as unknown as { process: { getBuiltinModule(name: string): unknown } }).process.getBuiltinModule('node:fs') as { readFileSync(path: URL, encoding: 'utf8'): string }
const read = (path: string) => fs.readFileSync(new URL(path, import.meta.url), 'utf8')
const dock = read('./bottom-nav.css')
const tokens = read('../styles/tokens.css')

describe('the phone layers move with the shared motion tokens (kit "A · iOS Yay")', () => {
  it('has no motion constant of its own: every animation and transition in bottom-nav.css takes a --rv- time and curve', () => {
    const lines = dock.split('\n').flatMap((line: string) => line.match(/(?:animation|transition):[^;}]*/g) ?? [])
    expect(lines.length).toBeGreaterThan(8)
    for (const line of lines) {
      expect(line, line).not.toMatch(/(?<![\w.-])(?:[1-9]\d*|0?\.\d+)m?s\b/) // a bare time such as 180ms or .2s (0s is no time)
      expect(line, line).not.toMatch(/(^|[ ,])ease(-in|-out|-in-out)?([ ,;]|$)/) // a bare curve
      if (!/none/.test(line) && !/--rv-topbar-offset/.test(line) || /--rv-topbar-offset/.test(line)) expect(line, line).toMatch(/var\(--rv-/)
    }
  })

  it('rises the menu and drops the notification sheet on the sheet time and curve, as every phone window does', () => {
    expect(dock).toMatch(/\.rv-fullmenu \{[^}]*animation: rv-rise var\(--rv-motion-sheet\) var\(--rv-ease-sheet\)/)
    expect(dock).toMatch(/rv-notification-history \{[^}]*animation: rv-sheet-down var\(--rv-motion-sheet\) var\(--rv-ease-sheet\)/)
    expect(read('../styles/responsive.css')).toContain('@keyframes rv-rise { from { translate: 0 100%; } to { translate: 0 0; } }')
    expect(tokens).toContain('--rv-motion-bar: var(--rv-motion-base);')
    expect(tokens).toContain('--rv-ease-bar: var(--rv-ease-sheet);')
  })

  it('slides the top bar on the bar tokens, and what sticks under it has a top that never changes (no animated offset, no dvh)', () => {
    expect(dock).not.toContain('--rv-topbar-offset')
    expect(dock).not.toMatch(/min-height: 100dvh/) // the page never re-lays-out with the browser toolbar; dvh stays on the full-screen layers only
    expect(dock).toContain('min-height: 100svh;')
    expect(read('../styles/responsive.css')).toContain('html body .rv-topbar { transition: transform var(--rv-motion-bar) var(--rv-ease-bar);')
    expect(read('../styles/design-system.css')).toContain('transition: transform var(--rv-motion-bar) var(--rv-ease-bar); will-change: transform; }')
    expect(read('../features/catalog/product-editor/product-editor-design-system.css')).toContain('.variant-table-head { top: calc(61px + var(--m-safe-top, 0px)); }')
    expect(read('../features/catalog/products-responsive.css')).not.toContain('--rv-topbar-offset')
    expect(read('../styles/responsive.css')).toContain('html body .app-shell.stitch-shell .rv-topbar { border-bottom-left-radius: 0; border-bottom-right-radius: 0; }')
  })
})

describe('useStuckHead', () => {
  it('marks the head while it sits below its natural place in the frame and clears it when it is back', () => {
    let top = 100
    function Probe() {
      const ref = useRef<HTMLDivElement>(null)
      useStuckHead(ref)
      return <div data-frame><div ref={ref} data-head /></div>
    }
    const host = document.createElement('div')
    document.body.append(host)
    const frame = () => host.querySelector('[data-frame]') as HTMLElement
    const root = createRoot(host)
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    const rects = (head: HTMLElement) => {
      frame().getBoundingClientRect = () => ({ top: 100, bottom: 500, left: 0, right: 0, width: 0, height: 0, x: 0, y: 100, toJSON() { return null } })
      head.getBoundingClientRect = () => ({ top, bottom: top + 40, left: 0, right: 0, width: 0, height: 0, x: 0, y: top, toJSON() { return null } })
    }
    act(() => root.render(<Probe />))
    const head = host.querySelector('[data-head]') as HTMLElement
    rects(head)
    const frameTick = () => act(async () => { window.dispatchEvent(new Event('scroll')); await new Promise(resolveFrame => requestAnimationFrame(() => resolveFrame(null))) })
    return (async () => {
      await frameTick()
      expect(head.hasAttribute('data-stuck')).toBe(false)
      top = 101
      await frameTick()
      expect(head.hasAttribute('data-stuck')).toBe(false) // 1 px down is inside the hysteresis band
      top = 161
      await frameTick()
      expect(head.hasAttribute('data-stuck')).toBe(true)
      top = 100
      await frameTick()
      expect(head.hasAttribute('data-stuck')).toBe(false)
      act(() => root.unmount())
      host.remove()
    })()
  })
})

describe('the dock counts', () => {
  it('hang off the icon, 9 px up and 3 px past its right edge, 16 px high with a ring in the part colour (10/700 text, 99+ at most)', () => {
    expect(dock).toMatch(/\.rv-dock-badge \{ position: absolute; top: -8px; right: -5px;[^}]*min-width: 14px; height: 14px; padding: 0 3px;[^}]*0 0 0 1\.5px var\(--rv-dock-ring, var\(--rv-color-surface\)\);[^}]*font-size: 9px; font-weight: 700;/)
  })
})

describe('the status bar area stays unpainted', () => {
  it('sets the see-through state deliberately: Home Screen app under a translucent status bar, no theme-color, no fixed or sticky surface within 10 px of the top edge', () => {
    const html = read('../../index.html')
    expect(html).toContain('<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />')
    expect(html).toContain('<meta name="apple-mobile-web-app-capable" content="yes" />')
    expect(html).toContain('viewport-fit=cover')
    expect(html).not.toMatch(/<meta name="theme-color"/)
    expect(dock).not.toContain('--rv-top-gap')
    expect(dock).toContain('.rv-fullmenu { position: fixed; inset: 0; z-index: 86; display: flex; flex-direction: column; box-sizing: border-box; padding: calc(env(safe-area-inset-top, 0px) + 8px) 0 0;')
  })
  it('keeps the top bar at the very top edge, its padding the safe area (one piece with the clock area), and slides it away whole', () => {
    expect(dock).toContain('.rv-topbar { top: 0; margin-top: 0; }')
    expect(dock).toContain('.rv-topbar.is-hidden { transform: translateY(calc(-100% - 1px)); }')
    expect(read('../styles/phone-spacing.css')).toContain('.rv-topbar { padding-top: var(--m-safe-top) !important; min-height: calc(62px + var(--m-safe-top)) !important; }')
  })

})

describe('layers over the top bar and the menu leaving', () => {
  it('lifts the filter block above the top bar while a layer is open, so its scrim covers the bar', () => {
    expect(dock).toContain('html[data-rv-layer] body .app-shell.stitch-shell main :is(.rr-fshell, .invoice-filter-scope, .ops-filters):has(.rr-fopen) { z-index: 901; }')
  })

  it('keeps the full-screen menu mounted for the shared exit (rv-sink on the exit tokens) with no touch while it goes', () => {
    expect(dock).toContain('.rv-fullmenu.is-closing { pointer-events: none; animation: rv-sink var(--rv-motion-exit) var(--rv-ease-exit) forwards; }')
    expect(read('../styles/responsive.css')).toContain('animation: rv-sink var(--rv-motion-exit) var(--rv-ease-exit) forwards')
    expect(tokens).toContain('--rv-motion-exit: 180ms;')
  })

  it('useExitPresence is present while open and leaving, closing only while it leaves', async () => {
    const { useExitPresence } = await import('./BottomDock')
    const seen: Array<[boolean, boolean]> = []
    function Probe({ open }: { open: boolean }) {
      const state = useExitPresence(open)
      seen.push([state.present, state.closing])
      return null
    }
    const host = document.createElement('div')
    const root = createRoot(host)
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    act(() => root.render(<Probe open />))
    expect(seen.at(-1)).toEqual([true, false])
    act(() => root.render(<Probe open={false} />))
    expect(seen.at(-1)).toEqual([true, true])
    await act(async () => { await new Promise(resolveWait => setTimeout(resolveWait, 260)) })
    expect(seen.at(-1)).toEqual([false, false])
    act(() => root.unmount())
  })
})
