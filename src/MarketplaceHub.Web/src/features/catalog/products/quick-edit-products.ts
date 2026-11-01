// The products a quick edit window works on, read one by one from the server while it is open. The products list
// holds only its current page, and a product kept from another page (or dropped from the page by a filter after the
// save) would stay at the version it had when it was selected: every later save of it would be refused (409/412).
// A product that cannot be read keeps the copy the page had, so the window does not lose a row.
export async function loadProductsByIds<P extends { id: string }>(ids: readonly string[], read: (id: string) => Promise<P>, fallback: (id: string) => P | undefined, concurrency = 6): Promise<P[]> {
  const results: Array<P | undefined> = new Array(ids.length)
  let next = 0
  async function worker() {
    while (next < ids.length) {
      const index = next++
      const id = ids[index]
      results[index] = await read(id).catch(() => fallback(id))
    }
  }
  await Promise.all(Array.from({ length: Math.min(concurrency, ids.length) }, worker))
  return results.filter((product): product is P => Boolean(product))
}

// A stock cell after its own save: it keeps showing the saved value until the product read again carries a new
// stock. Until then the variant still holds the stock from before the save, and showing (and saving against) that
// one would undo the change on screen and make the next save fail as stale.
export type SavedStock = { value: number; previousOnHand: number }
export function quickStockCellValue(onHand: number, saved: SavedStock | null): { value: number; saved: SavedStock | null } {
  if (saved && onHand === saved.previousOnHand) return { value: saved.value, saved }
  return { value: onHand, saved: null }
}
