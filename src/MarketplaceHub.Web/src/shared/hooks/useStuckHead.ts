import { useEffect, type RefObject } from 'react'

// A sticky head knows when it is stuck: it has moved down from its natural place (the top of the frame it belongs to).
// The head gets data-stuck while that is so, which style sheets use to square its corners (they are round only while it sits
// in its frame) and the top bar uses to stay shown (App.tsx: while a head is stuck nothing under it may move). Measured from the
// two boxes on every scroll frame, with a 2 px hysteresis (stuck from 2 px, free again at 0.5 px) so it cannot flicker at the
// threshold. The attribute is written to the element directly: nothing re-renders. stuckChangeEvent tells the shell.
export const stuckChangeEvent = 'rv-stuck-change'

export function useStuckHead(head: RefObject<HTMLElement | null>) {
  useEffect(() => {
    const element = head.current
    const frame = element?.parentElement
    if (!element || !frame) return undefined
    let raf = 0
    const measure = () => {
      raf = 0
      const displaced = element.getBoundingClientRect().top - (frame.getBoundingClientRect().top + frame.clientTop)
      const was = element.hasAttribute('data-stuck')
      const stuck = was ? displaced > 0.5 : displaced > 2
      if (stuck === was) return
      if (stuck) element.setAttribute('data-stuck', ''); else element.removeAttribute('data-stuck')
      window.dispatchEvent(new Event(stuckChangeEvent))
    }
    const schedule = () => { if (!raf) raf = requestAnimationFrame(measure) }
    window.addEventListener('scroll', schedule, { passive: true })
    window.addEventListener('resize', schedule)
    schedule()
    return () => {
      cancelAnimationFrame(raf)
      window.removeEventListener('scroll', schedule)
      window.removeEventListener('resize', schedule)
      if (element.hasAttribute('data-stuck')) { element.removeAttribute('data-stuck'); window.dispatchEvent(new Event(stuckChangeEvent)) }
    }
  }, [head])
}
