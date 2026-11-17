import { describe, expect, it } from 'vitest'
import { cursorPageParams, offsetCursor } from './paging'

describe('offset cursor', () => {
  // The server's OffsetCursor.Encode: base64url of "o" and the offset, no padding (ApiPagingContractTests).
  it('writes the server cursor of an offset', () => {
    expect(offsetCursor(40)).toBe('bzQw')
    expect(offsetCursor(20)).toBe('bzIw')
    expect(offsetCursor(1000)).toBe('bzEwMDA')
    expect(atob(offsetCursor(150))).toBe('o150')
  })

  it('asks the first page without a cursor and a later page by its first row', () => {
    expect(cursorPageParams(new URLSearchParams(), 0, 50).toString()).toBe('limit=50')
    expect(cursorPageParams(new URLSearchParams(), 2, 20).toString()).toBe('limit=20&after=bzQw')
  })
})
