import { describe, expect, it } from 'vitest'
import { shouldResetScroll } from './page-scroll'

describe('shouldResetScroll', () => {
  const base = { pathname: '/products', lastPathname: '/orders', locked: false, navigationType: 'PUSH' }
  it('resets when the path changes by a push or a replace (dock, menu, links, search)', () => {
    expect(shouldResetScroll(base)).toBe(true)
    expect(shouldResetScroll({ ...base, navigationType: 'REPLACE' })).toBe(true)
    expect(shouldResetScroll({ ...base, lastPathname: null, navigationType: 'POP' })).toBe(true)
  })
  it('keeps the position for a query or tab change on the same page and for back / forward', () => {
    expect(shouldResetScroll({ ...base, lastPathname: '/products' })).toBe(false)
    expect(shouldResetScroll({ ...base, navigationType: 'POP' })).toBe(false)
  })
  it('waits while the menu or a sheet holds the page, so opening and closing one moves nothing', () => {
    expect(shouldResetScroll({ ...base, locked: true })).toBe(false)
    expect(shouldResetScroll({ ...base, locked: true, lastPathname: '/products' })).toBe(false)
  })
})
