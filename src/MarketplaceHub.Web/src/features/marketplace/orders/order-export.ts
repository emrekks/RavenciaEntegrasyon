// Reading every page of the order list (the CSV export, and the list while the label filter is applied here) and what
// the export says when it is done.

type OrderPage<T> = { items: T[]; hasMore: boolean; totalCount?: number | null }

export const orderPageLimit = 200
export const orderMaxPages = 100
// The most orders one export reads: 100 pages of 200.
export const orderExportCap = orderPageLimit * orderMaxPages

// Every page until the list ends or the cap is reached. `truncated` says that orders were left out at the cap, and
// `totalCount` how many the server counted, so the export can say it took only the first ones instead of stopping
// silently.
export async function readAllOrderPages<T>(loadPage: (limit: number, page: number) => Promise<OrderPage<T>>): Promise<{ items: T[]; truncated: boolean; totalCount: number }> {
  const items: T[] = []
  let totalCount = 0
  let more = false
  for (let page = 1; page <= orderMaxPages; page++) {
    const result = await loadPage(orderPageLimit, page)
    items.push(...result.items)
    totalCount = result.totalCount ?? items.length
    more = result.hasMore && result.items.length > 0 && items.length < totalCount
    if (!more) break
  }
  return { items, truncated: more, totalCount: Math.max(totalCount, items.length) }
}

// The orders an export writes: the server's read when the server filters "Etiket Durumu" (by labelPrintedAt, as the
// list does), else the list's own, already filtered orders. Never filtered again with this browser's print marks, so
// the file holds what the list shows and the same on every computer.
export function ordersToExport<T>(listItems: readonly T[], serverRead: { items: readonly T[] } | undefined): T[] {
  return [...(serverRead ? serverRead.items : listItems)]
}

// The notice after an export: how many orders went to the file and, when the cap cut it, that only the first ones did.
export function orderExportNotice(exported: number, read: { truncated: boolean; totalCount: number }): { message: string; kind: 'success' | 'info' } {
  const count = (value: number) => value.toLocaleString('tr-TR')
  if (!read.truncated) return { message: `${count(exported)} sipariş dışa aktarıldı.`, kind: 'success' }
  return { message: `İlk ${count(exported)} sipariş dışa aktarıldı; filtreye uyan ${count(read.totalCount)} sipariş var. Kalanlar için tarih aralığını daraltıp yeniden dışa aktarın.`, kind: 'info' }
}
