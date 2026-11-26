import { Fragment, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { flushSync } from 'react-dom'

// A report block below the first screen is drawn after it (performance audit 26 Nov 2026: laying out every card of a
// tab at once took ~0.5 s on a 4x slowed phone, most of it style and layout of cards nobody sees yet). Until then an
// empty box of about its height holds its place, below the screen, so nothing on screen moves. A block mounts:
// - before the first paint when any of it is already on screen (checked in a layout effect, so it is never painted empty);
// - when it comes within `margin` of the screen while scrolling;
// - otherwise one block at a time while the browser is idle, in page order, so after a moment the whole tab is there
//   (find in page, printing) without one long task;
// - at once before printing.
// Once mounted it renders its children exactly as before: no wrapper of its own, the same markup and figures.
// Without IntersectionObserver (tests, old browsers) everything renders at once, as it always did.

type Waiting = { mount: () => void }
const waiting: Waiting[] = []
let scheduled = false
let printHooked = false

const whenIdle = (run: () => void) => {
  if (typeof requestIdleCallback === 'function') requestIdleCallback(run, { timeout: 400 })
  else setTimeout(run, 30)
}

function mountNext() {
  if (scheduled || !waiting.length) return
  scheduled = true
  whenIdle(() => {
    scheduled = false
    waiting.shift()?.mount()
    mountNext()
  })
}

// Printing prints the whole tab: every waiting block mounts before the page is laid out for paper.
function mountAllForPrint() {
  flushSync(() => { for (const entry of waiting.splice(0)) entry.mount() })
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

export function Deferred({ estimate, margin = 600, children }: { estimate: number; margin?: number; children: ReactNode }) {
  const [mounted, setMounted] = useState(() => !canDefer())
  const box = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    const element = box.current
    if (mounted || !element) return undefined
    if (element.getBoundingClientRect().top < window.innerHeight) { setMounted(true); return undefined }
    if (!printHooked) { printHooked = true; window.addEventListener('beforeprint', mountAllForPrint) }
    const entry: Waiting = { mount: () => setMounted(true) }
    waiting.push(entry)
    mountNext()
    const observer = new IntersectionObserver(entries => { if (entries.some(item => item.isIntersecting)) setMounted(true) }, { root: scrollRoot(element), rootMargin: `0px 0px ${margin}px 0px` })
    observer.observe(element)
    return () => {
      observer.disconnect()
      const index = waiting.indexOf(entry)
      if (index >= 0) waiting.splice(index, 1)
    }
  }, [mounted, margin])
  // Keyed apart, so the block's own first element is created afresh and never reuses the placeholder's.
  return mounted ? <Fragment key="block">{children}</Fragment> : <div key="placeholder" ref={box} data-deferred="" aria-hidden="true" style={{ minHeight: estimate }} />
}
