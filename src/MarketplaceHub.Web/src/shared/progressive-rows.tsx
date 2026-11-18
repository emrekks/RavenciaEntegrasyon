import { startTransition, useCallback, useEffect, useRef, useState } from 'react'

// A long list (Siparişler, İadeler, Faturalar, Ürünler, İşlem Takibi) draws its first rows in the frame that shows a new
// tab, filter or page and the rest right after that frame is on screen. Drawing 50 order rows at once kept a tab change
// ~1 s on a 4x slowed phone (React, style and layout all grow with every row); the first rows fill the screen anyway.
// Until the rest arrive, a spacer as tall as they were last time keeps the page and its scrollbar the same length.
// When the page is already scrolled down (a refetch, a new order on top while reading row 40), the list is drawn whole,
// so nothing the person is looking at ever disappears for a frame.
export const firstFrameRows = 12
// A phone shows two or three order cards per screen and lays each out in ~30 ms on a 4x slowed CPU: six fill it.
export const firstFrameRowsOnPhone = 6
const firstRows = () => typeof window !== 'undefined' && window.matchMedia?.('(max-width: 760px)').matches ? firstFrameRowsOnPhone : firstFrameRows

function pageScrolled() {
  if (typeof window === 'undefined') return false
  if (window.scrollY > 120) return true
  return Array.from(document.querySelectorAll<HTMLElement>('.app-shell > main > :is(.content, .rv-page)')).some(scroller => scroller.scrollTop > 120)
}

export function useProgressiveRows<T>(rows: readonly T[], idOf: (row: T) => string) {
  const signature = rows.map(idOf).join('\u0001')
  const [state, setState] = useState({ signature, complete: true })
  let current = state
  if (state.signature !== signature) {
    // A new set of rows: draw the first ones now unless the page is scrolled down (read before this render is committed).
    current = { signature, complete: rows.length <= firstRows() || pageScrolled() }
    setState(current)
  }
  const rowHeight = useRef(0)
  const container = useRef<HTMLElement | null>(null)
  const containerRef = useCallback((element: HTMLElement | null) => { container.current = element }, [])
  const { complete } = current
  useEffect(() => {
    let timer = 0
    // A frame callback runs before the paint, a task queued from it after: the rest follow once the first rows are seen,
    // as an interruptible update. Once all rows are on screen, their height is read (the layout is clean by then).
    const frame = window.requestAnimationFrame(() => {
      timer = window.setTimeout(() => {
        if (!complete) { startTransition(() => setState(value => value.signature === signature ? { signature, complete: true } : value)); return }
        const element = container.current
        if (element && rows.length > 0) { const height = element.getBoundingClientRect().height / rows.length; if (height > 0) rowHeight.current = height }
      }, 0)
    })
    return () => { window.cancelAnimationFrame(frame); window.clearTimeout(timer) }
  }, [complete, signature, rows.length])
  const shown = complete ? rows : rows.slice(0, firstRows())
  return { rows: shown, pendingHeight: complete ? 0 : Math.round((rows.length - shown.length) * rowHeight.current), containerRef }
}

/** The stand-in for the rows still to come: as tall as they were, empty, hidden from assistive technology. */
export function PendingRows({ height, colSpan, as = 'tr' }: { height: number; colSpan?: number; as?: 'tr' | 'div' }) {
  if (height <= 0) return null
  const style = { height, padding: 0, border: 0 }
  return as === 'tr' ? <tr className="rv-rows-pending" aria-hidden="true"><td colSpan={colSpan} style={style} /></tr> : <div className="rv-rows-pending" aria-hidden="true" style={style} />
}
