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

  it('moves the top bar and what sticks under it on one time and curve, with a registered length', () => {
    expect(dock).toContain('@property --rv-topbar-offset { syntax: "<length>"; inherits: true; initial-value: 0px; }')
    expect(dock).toContain('transition: --rv-topbar-offset var(--rv-motion-bar) var(--rv-ease-bar)')
    expect(dock).toContain('.app-shell.stitch-shell:has(.rv-topbar.is-hidden) { --rv-topbar-offset: 0px; }')
    expect(read('../styles/responsive.css')).toContain('html body .rv-topbar { transition: transform var(--rv-motion-bar) var(--rv-ease-bar);')
    expect(read('../styles/design-system.css')).toContain('transition: transform var(--rv-motion-bar) var(--rv-ease-bar); will-change: transform; }')
    expect(read('../features/catalog/product-editor/product-editor-design-system.css')).toContain('.variant-table-head { top: var(--rv-topbar-offset); }')
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
