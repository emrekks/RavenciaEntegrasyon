// Each unit price sits level with its product line in the desktop order and return tables (.rr-pr beside .rr-pline).
// Every row asks through one queue, flushed once per frame before paint: all rows are measured first and written
// after, instead of each row's write forcing the next row's read to lay the whole page out again (a 50-row page used
// to mean 50 layouts whenever its width changed, e.g. while the menu was pinned).
const pending = new Set<HTMLElement>()
let frame = 0

export function flushUnitPriceAlignment() {
  if (frame) cancelAnimationFrame(frame)
  frame = 0
  const table = window.innerWidth > 1024
  const rows = [...pending]
  pending.clear()
  const heights = rows.flatMap(row => {
    if (!row.isConnected) return []
    const products = row.querySelectorAll<HTMLElement>('.rr-pline')
    return [...row.querySelectorAll<HTMLElement>('.rr-pr')].map((price, index) => [price, table && products[index] ? `${products[index].getBoundingClientRect().height}px` : ''] as const)
  })
  heights.forEach(([price, height]) => { if (price.style.height !== height) price.style.height = height })
}

export function queueUnitPriceAlignment(row: HTMLElement) {
  pending.add(row)
  if (!frame) frame = requestAnimationFrame(flushUnitPriceAlignment)
}

/** Aligns the row before the next paint and again whenever one of its product lines changes size; returns the cleanup. */
export function observeUnitPriceAlignment(row: HTMLElement): () => void {
  queueUnitPriceAlignment(row)
  if (typeof ResizeObserver === 'undefined') return () => { pending.delete(row) }
  const observer = new ResizeObserver(() => queueUnitPriceAlignment(row))
  row.querySelectorAll('.rr-pline').forEach(element => observer.observe(element))
  return () => { observer.disconnect(); pending.delete(row) }
}
