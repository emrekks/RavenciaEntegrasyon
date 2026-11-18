import { useCallback, useRef, useState, type RefObject, type UIEvent } from 'react'
import { useMediaQuery } from '../../shared/hooks/useMediaQuery'
import './product-lines.css'

// A row of the order / return table with many products must not grow without end (owner, 18 Nov 2026):
// - desktop: from the fifth product on, the product list keeps the height of four and scrolls inside its cell; the unit
//   price cell beside it scrolls with it (one linked scroll), and a "+N ürün" hint sits at the foot until the end is seen;
// - phone (cards): three products show and "N ürün daha" opens the rest, no nested scrolling.
export const LINES_BEFORE_SCROLL = 4
export const PHONE_LINES = 3

export type ProductLinesView = {
  // How many lines to draw (all of them except on a phone card that is not opened).
  limit: number
  scrolls: boolean
  phone: boolean
  hiddenOnPhone: number
  expanded: boolean
  toggleExpanded: () => void
  linesRef: RefObject<HTMLDivElement | null>
  pricesRef: RefObject<HTMLDivElement | null>
  linesScroll: (event: UIEvent<HTMLDivElement>) => void
  pricesScroll: (event: UIEvent<HTMLDivElement>) => void
  // "+N ürün" while more products lie below the visible ones, else empty.
  moreHint: string
}

export function useProductLines(total: number): ProductLinesView {
  const phone = useMediaQuery('(max-width: 760px)')
  const [expanded, setExpanded] = useState(false)
  const [atEnd, setAtEnd] = useState(false)
  const scrolls = !phone && total > LINES_BEFORE_SCROLL
  const linesRef = useRef<HTMLDivElement>(null)
  const pricesRef = useRef<HTMLDivElement>(null)
  // The scroll one cell reports is copied to the other once; the copy's own event finds them level and stops.
  const link = useCallback((from: RefObject<HTMLDivElement | null>, to: RefObject<HTMLDivElement | null>) => (event: UIEvent<HTMLDivElement>) => {
    const source = event.currentTarget
    const other = to.current
    if (other && Math.abs(other.scrollTop - source.scrollTop) > 0.5) other.scrollTop = source.scrollTop
    if (from.current === source) setAtEnd(source.scrollTop + source.clientHeight >= source.scrollHeight - 2)
  }, [])
  const limit = phone && !expanded ? PHONE_LINES : total
  return {
    limit, scrolls, phone,
    hiddenOnPhone: phone ? Math.max(0, total - PHONE_LINES) : 0,
    expanded, toggleExpanded: () => setExpanded(value => !value),
    linesRef, pricesRef,
    linesScroll: link(linesRef, pricesRef),
    pricesScroll: link(pricesRef, linesRef),
    moreHint: scrolls && !atEnd ? `+${total - LINES_BEFORE_SCROLL} ürün` : ''
  }
}
