import { describe, expect, it, vi } from 'vitest'
import { orderExportCap, orderExportNotice, ordersToExport, readAllOrderPages } from './order-export'

const pages = (total: number) => vi.fn(async (limit: number, page: number) => {
  const start = (page - 1) * limit
  const count = Math.max(0, Math.min(limit, total - start))
  return { items: Array.from({ length: count }, (_, index) => start + index), hasMore: start + count < total, totalCount: total }
})

describe('order export', () => {
  it('reads every page while the list fits under the cap', async () => {
    const load = pages(450)
    const read = await readAllOrderPages(load)
    expect(read.items).toHaveLength(450)
    expect(read.truncated).toBe(false)
    expect(load).toHaveBeenCalledTimes(3)
    expect(orderExportNotice(450, read)).toEqual({ message: '450 sipariş dışa aktarıldı.', kind: 'success' })
  })

  it('says plainly that only the first 20.000 went to the file when the list is longer', async () => {
    const read = await readAllOrderPages(pages(orderExportCap + 1234))
    expect(read.items).toHaveLength(orderExportCap)
    expect(read.truncated).toBe(true)
    expect(read.totalCount).toBe(21234)
    expect(orderExportNotice(read.items.length, read)).toEqual({ message: 'İlk 20.000 sipariş dışa aktarıldı; filtreye uyan 21.234 sipariş var. Kalanlar için tarih aralığını daraltıp yeniden dışa aktarın.', kind: 'info' })
  })

  it('writes the orders the server filtered by labelPrintedAt, not this browser\'s print marks', () => {
    // "Etiket Basılmadı": the server lists order 2 as not printed although this browser printed it once; it stays.
    const serverRead = { items: [{ id: '1' }, { id: '2' }] }
    expect(ordersToExport([{ id: 'x' }], serverRead).map(item => item.id)).toEqual(['1', '2'])
    // An older server that cannot filter the label: the list's own (locally filtered) orders, as shown.
    expect(ordersToExport([{ id: 'x' }], undefined).map(item => item.id)).toEqual(['x'])
  })

  it('is not cut when the list ends exactly at the cap', async () => {
    const read = await readAllOrderPages(pages(orderExportCap))
    expect(read.truncated).toBe(false)
  })
})
