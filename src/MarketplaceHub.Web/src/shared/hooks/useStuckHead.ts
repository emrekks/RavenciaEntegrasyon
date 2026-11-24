import { useEffect, type RefObject } from 'react'

// A sticky head knows when it is stuck: it has moved down from its natural place (the top of the frame it belongs to).
// The head gets data-stuck while that is so, which style sheets use to square its corners (they are round only while it sits
// in its frame). Measured from the two boxes on every scroll frame, with the top bar's class changes (its slide moves the
// head's offset) and a resize, so there is no state to go stale and nothing re-renders.
export function useStuckHead(head: RefObject<HTMLElement | null>) {
  useEffect(() => {
    const element = head.current
    const frame = element?.parentElement
    if (!element || !frame) return undefined
    let raf = 0
    let timer = 0
    const measure = () => {
      raf = 0
      const natural = frame.getBoundingClientRect().top + frame.clientTop
      const stuck = element.getBoundingClientRect().top - natural > 0.5
      if (stuck) { if (!element.hasAttribute('data-stuck')) element.setAttribute('data-stuck', '') } else element.removeAttribute('data-stuck')
    }
    const schedule = () => { if (!raf) raf = requestAnimationFrame(measure) }
    window.addEventListener('scroll', schedule, { passive: true })
    window.addEventListener('resize', schedule)
    const bar = document.querySelector('.rv-topbar')
    const observer = bar ? new MutationObserver(() => { schedule(); window.clearTimeout(timer); timer = window.setTimeout(schedule, 400) }) : null
    if (bar) observer?.observe(bar, { attributes: true, attributeFilter: ['class'] })
    schedule()
    return () => { cancelAnimationFrame(raf); window.clearTimeout(timer); window.removeEventListener('scroll', schedule); window.removeEventListener('resize', schedule); observer?.disconnect(); element.removeAttribute('data-stuck') }
  }, [head])
}
