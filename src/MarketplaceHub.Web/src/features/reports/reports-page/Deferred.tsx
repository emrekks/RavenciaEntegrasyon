import { Fragment, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { flushSync } from 'react-dom'

// A report block below the first screen is drawn after it (performance audit 26 Nov 2026: laying out every card of a
// tab at once took ~0.5 s on a 4x slowed phone, most of it style and layout of cards nobody sees yet). Until then an
// empty box of its height holds its place, with the grid classes the block has (`className`), so nothing moves when it
// comes. A block mounts:
// - before the first paint when any of it is already on screen (checked in a layout effect, so it is never painted empty);
// - as soon as it comes within a screen above or two screens below the visible part while scrolling;
// - otherwise one block after another while the browser is idle, in page order, so after a moment the whole tab is
//   there (find in page, printing) without one long task; all at once while the page is hidden;
// - at once before printing.
// Once mounted it renders its children exactly as before: no wrapper of its own, the same markup and figures.
// Without IntersectionObserver (tests, old browsers) everything renders at once, as it always did.
//
// The box's height: the block's own height measured the last time it was drawn at this window width (a tab opened
// again), else the estimate for the width (phone up to 760 px, tablet up to 1024, laptop, wide screen from 1600).

export type Estimate = number | readonly [phone: number, tablet: number, desktop: number, wide?: number]

export function estimateFor(estimate: Estimate, width: number) {
  if (typeof estimate === 'number') return estimate
  const [phone, tablet, desktop, wide] = estimate
  return width <= 760 ? phone : width <= 1024 ? tablet : width >= 1600 && wide !== undefined ? wide : desktop
}

type Waiting = { mount: () => void }
const waiting: Waiting[] = []
let scheduled = false
let hooked = false
// The heights blocks had when drawn, by block and window width.
const drawnHeights = new Map<string, number>()

function mountAll() {
  flushSync(() => { for (const entry of waiting.splice(0)) entry.mount() })
}

// One chain: the next block when the browser is idle (or after 50 ms at most, so a page that is not painted still
// fills in), all of them while the page is hidden.
function mountNext() {
  if (!waiting.length) return
  if (document.visibilityState === 'hidden') { for (const entry of waiting.splice(0)) entry.mount(); return }
  if (scheduled) return
  scheduled = true
  const run = () => {
    scheduled = false
    waiting.shift()?.mount()
    mountNext()
  }
  if (typeof requestIdleCallback === 'function') requestIdleCallback(run, { timeout: 50 })
  else setTimeout(run, 16)
}

function hook() {
  if (hooked) return
  hooked = true
  window.addEventListener('beforeprint', mountAll)
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') mountNext() })
}

// The element whose scrolling moves the block (the report's own scroller on a laptop, null for the window on a phone).
function scrollRoot(element: HTMLElement): Element | null {
  for (let parent = element.parentElement; parent && parent !== document.body; parent = parent.parentElement) {
    const overflow = getComputedStyle(parent).overflowY
    if (overflow === 'auto' || overflow === 'scroll') return parent
  }
  return null
}

const canDefer = () => typeof IntersectionObserver !== 'undefined' && typeof window !== 'undefined'

// Where the box sat in its parent, to find the block that took its place.
type Spot = { parent: HTMLElement; left: number; top: number }
function spotOf(element: HTMLElement): Spot | null {
  const parent = element.parentElement
  if (!parent) return null
  const box = element.getBoundingClientRect()
  const frame = parent.getBoundingClientRect()
  return { parent, left: box.left - frame.left, top: box.top - frame.top }
}

export function Deferred({ name, estimate, className, boxStyle, children }: { name: string; estimate: Estimate; className?: string; boxStyle?: CSSProperties; children: ReactNode }) {
  const [mounted, setMounted] = useState(() => !canDefer())
  const box = useRef<HTMLDivElement>(null)
  const spot = useRef<Spot | null>(null)
  const key = typeof window === 'undefined' ? name : `${name}@${window.innerWidth}`
  useLayoutEffect(() => {
    const element = box.current
    if (mounted) {
      // The block's height, read once where the box was: the first element now at its place.
      const at = spot.current
      spot.current = null
      if (!at || !at.parent.isConnected) return undefined
      const frame = at.parent.getBoundingClientRect()
      const block = [...at.parent.children].find(child => {
        if (child.hasAttribute('data-deferred')) return false
        const rect = child.getBoundingClientRect()
        return Math.abs(rect.left - frame.left - at.left) < 1 && Math.abs(rect.top - frame.top - at.top) < 1
      })
      if (block) drawnHeights.set(key, block.getBoundingClientRect().height)
      return undefined
    }
    if (!element) return undefined
    const mount = () => { spot.current = spotOf(element); setMounted(true) }
    if (element.getBoundingClientRect().top < window.innerHeight) { mount(); return undefined }
    hook()
    const entry: Waiting = { mount }
    waiting.push(entry)
    mountNext()
    const observer = new IntersectionObserver(entries => { if (entries.some(item => item.isIntersecting)) mount() }, { root: scrollRoot(element), rootMargin: '100% 0px 200% 0px' })
    observer.observe(element)
    return () => {
      observer.disconnect()
      const index = waiting.indexOf(entry)
      if (index >= 0) waiting.splice(index, 1)
    }
  }, [mounted, key])
  // Keyed apart, so the block's own first element is created afresh and never reuses the placeholder's.
  if (mounted) return <Fragment key="block">{children}</Fragment>
  const height = drawnHeights.get(key) ?? estimateFor(estimate, typeof window === 'undefined' ? 1280 : window.innerWidth)
  return <div key="placeholder" ref={box} className={className} data-deferred={name} aria-hidden="true" style={{ ...boxStyle, minHeight: height }} />
}
