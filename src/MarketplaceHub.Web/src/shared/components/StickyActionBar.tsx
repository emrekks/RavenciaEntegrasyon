import { useEffect, useRef, useState, type ReactNode } from 'react'
import './sticky-action-bar.css'

const exitMs = 160
// A page bar floats this far above the window's bottom edge while it sticks (see sticky-action-bar.css).
const stickOffset = 12

function scrollParent(element: HTMLElement): HTMLElement | null {
  for (let node = element.parentElement; node; node = node.parentElement) {
    const overflowY = window.getComputedStyle(node).overflowY
    if (overflowY === 'auto' || overflowY === 'scroll') return node
  }
  return null
}

// The bottom bar of a page's selection or save actions. It docks to the bottom edge of the content column with no
// gap (rows never scroll by under it) and stays in the page flow, so at the end of the list it sits after the last
// row instead of covering it. It slides in when it opens and out when it closes, showing what it showed last while
// it leaves (a cleared selection does not flash "0 seçildi").
export function StickyActionBar({ open = true, label, className, children }: { open?: boolean; label: string; className?: string; children: ReactNode }) {
  const [present, setPresent] = useState(open)
  if (open && !present) setPresent(true)
  const lastChildren = useRef<ReactNode>(children)
  useEffect(() => { if (open) lastChildren.current = children })
  useEffect(() => {
    if (open || !present) return undefined
    const timer = window.setTimeout(() => setPresent(false), exitMs)
    return () => window.clearTimeout(timer)
  }, [open, present])
  const barRef = useRef<HTMLDivElement>(null)
  // data-docked is true when the page has been scrolled to its end and the bar rests at its place in the flow (under
  // the last card, which then merges with it); false while it floats above the window's edge.
  useEffect(() => {
    const bar = barRef.current
    if (!bar) return undefined
    const scroller = scrollParent(bar)
    const target: HTMLElement | Window = scroller ?? window
    let frame = 0
    const update = () => {
      frame = 0
      const edge = scroller ? scroller.getBoundingClientRect().bottom : window.innerHeight
      bar.dataset.docked = String(bar.getBoundingClientRect().bottom < edge - stickOffset - 1)
    }
    const schedule = () => { if (!frame) frame = window.requestAnimationFrame(update) }
    update()
    target.addEventListener('scroll', schedule, { passive: true })
    window.addEventListener('resize', schedule)
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(schedule)
    observer?.observe(scroller ?? document.body)
    observer?.observe(bar)
    return () => {
      if (frame) window.cancelAnimationFrame(frame)
      target.removeEventListener('scroll', schedule)
      window.removeEventListener('resize', schedule)
      observer?.disconnect()
    }
  }, [present])
  if (!open && !present) return null
  return <div ref={barRef} className={`rv-action-bar${open ? '' : ' is-leaving'}${className ? ` ${className}` : ''}`} role="group" aria-label={label} inert={!open || undefined}>{open ? children : lastChildren.current}</div>
}
