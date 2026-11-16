// The two paging forms of the API (PagingContract, API-GUVENLIK-8): the cursor (limit + after → nextCursor) where a
// list's order gives one, and NumberedPage (page + pageSize) where it does not.

/**
 * The cursor of the page that starts at row `offset` of a list that pages by offset (OffsetCursor on the server: "o"
 * and the offset, base64url). A page jump sends it; the server derives the page from it and refuses one that does not
 * start a page of the asked limit.
 */
export function offsetCursor(offset: number): string {
  return btoa(`o${Math.max(0, Math.trunc(offset))}`).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

/** The cursor query of the 0-based page `pageIndex` of `limit` rows: limit, and after from the second page on. */
export function cursorPageParams(params: URLSearchParams, pageIndex: number, limit: number) {
  params.set('limit', String(limit))
  if (pageIndex > 0) params.set('after', offsetCursor(pageIndex * limit))
  return params
}
