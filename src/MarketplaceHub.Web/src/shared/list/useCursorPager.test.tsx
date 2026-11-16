import { act } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import { useCursorPager, type CursorPager } from './useCursorPager'

let pager: CursorPager
let drawn: Array<{ page: number; cursor: string | null }> = []

function Probe({ resetKey, initialPage, initialCursors }: { resetKey: string; initialPage?: number; initialCursors?: Record<number, string | null> }) {
  pager = useCursorPager({ resetKey, initialPage, initialCursors })
  drawn.push({ page: pager.page, cursor: pager.cursor })
  return null
}

afterEach(() => { cleanup(); drawn = [] })

describe('useCursorPager', () => {
  it('keeps the cursor of every page read and reads the page shown with its own', () => {
    render(<Probe resetKey="a" />)
    expect(pager.page).toBe(1)
    expect(pager.cursor).toBeNull()
    act(() => pager.rememberCursor(2, 'c2'))
    act(() => pager.setPage(current => current + 1))
    expect(pager.page).toBe(2)
    expect(pager.cursor).toBe('c2')
    expect(pager.knows(2)).toBe(true)
    expect(pager.knows(3)).toBe(false)
    expect(pager.cursorOf(3)).toBeNull()
  })

  it('opens the page and cursor an address carried', () => {
    render(<Probe resetKey="a" initialPage={3} initialCursors={{ 3: 'c3' }} />)
    expect(pager.page).toBe(3)
    expect(pager.cursor).toBe('c3')
  })

  it('a list change goes back to page 1 and forgets the cursors before anything is drawn with the old page', () => {
    const { rerender } = render(<Probe resetKey="a" />)
    act(() => pager.rememberCursor(2, 'c2'))
    act(() => pager.setPage(2))
    drawn = []
    rerender(<Probe resetKey="b" />)
    expect(drawn.every(entry => entry.page === 1 && entry.cursor === null)).toBe(true)
    expect(pager.knows(2)).toBe(false)
  })

  it('drops a cursor read for a list that has changed since (a page jump under way)', () => {
    const { rerender } = render(<Probe resetKey="a" />)
    rerender(<Probe resetKey="b" />)
    act(() => pager.rememberCursor(2, 'old-list', 'a'))
    expect(pager.knows(2)).toBe(false)
    act(() => pager.rememberCursor(2, 'c2', 'b'))
    expect(pager.cursorOf(2)).toBe('c2')
  })

  it('remembering a cursor already known renders nothing more', () => {
    render(<Probe resetKey="a" />)
    act(() => pager.rememberCursor(2, 'c2'))
    drawn = []
    act(() => pager.rememberCursor(2, 'c2'))
    act(() => pager.setPage(1))
    expect(drawn).toEqual([])
  })
})
