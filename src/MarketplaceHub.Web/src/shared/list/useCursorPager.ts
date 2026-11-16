import { useCallback, useRef, useState, type SetStateAction } from 'react'

export type CursorPagerOptions = {
  /** Everything that makes it another list (filters, search, tab, sort, page size): a change goes back to page 1 and
   * forgets the cursors read for the list before. */
  resetKey: string
  /** The page an address opened (W16). */
  initialPage?: number
  /** The cursors an address carried, by page (page 1 needs none). */
  initialCursors?: Record<number, string | null>
}

export type CursorPager = {
  page: number
  /** Stable between renders. */
  setPage: (next: SetStateAction<number>) => void
  /** The cursor that reads the page shown; null for page 1 and for a page whose cursor is not known yet. */
  cursor: string | null
  cursorOf: (page: number) => string | null
  /** A cursor for this page was read (page 1 always is). */
  knows: (page: number) => boolean
  /** Keeps the cursor an answer gave for a page; nothing happens when it is already known, so it may run while rendering.
   * Stable between renders, so an effect that calls it does not run again for it. With `forKey` (the resetKey the work
   * started on) a cursor read for a list that has since changed is dropped. */
  rememberCursor: (page: number, cursor: string | null, forKey?: string) => void
}

type PagerState = { page: number; cursors: Record<number, string | null>; key: string }

/**
 * The numbered pages of a list the server pages by cursor: the page shown, the cursor of every page read so far, and the
 * way back to page 1 when the list changes. The reset happens in the render that shows the other list, so no request
 * goes out for the new filters with the old page's cursor.
 */
export function useCursorPager({ resetKey, initialPage = 1, initialCursors = {} }: CursorPagerOptions): CursorPager {
  const [state, setState] = useState<PagerState>(() => ({ page: initialPage, cursors: { ...initialCursors, 1: null }, key: resetKey }))
  let current = state
  if (state.key !== resetKey) {
    current = { page: 1, cursors: { 1: null }, key: resetKey }
    setState(current)
  }
  // The state this render shows, for the stable callbacks' "already so" checks.
  const shown = useRef(current)
  shown.current = current
  const { page, cursors } = current
  const cursorOf = (target: number) => target > 1 ? cursors[target] ?? null : null

  const setPage = useCallback((next: SetStateAction<number>) => {
    if (next === shown.current.page) return
    setState(previous => {
      const target = typeof next === 'function' ? next(previous.page) : next
      return target === previous.page ? previous : { ...previous, page: target }
    })
  }, [])
  const rememberCursor = useCallback((target: number, cursor: string | null, forKey?: string) => {
    if (shown.current.cursors[target] === cursor && (forKey === undefined || shown.current.key === forKey)) return
    setState(previous => (forKey !== undefined && previous.key !== forKey) || previous.cursors[target] === cursor ? previous : { ...previous, cursors: { ...previous.cursors, [target]: cursor } })
  }, [])

  return {
    page,
    setPage,
    cursor: cursorOf(page),
    cursorOf,
    knows: target => target === 1 || cursors[target] !== undefined,
    rememberCursor
  }
}
