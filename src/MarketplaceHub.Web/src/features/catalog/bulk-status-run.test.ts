import { describe, expect, it, vi } from 'vitest'
import { bulkStatusPlan, bulkStatusResultMessage, runInChunks } from './bulk-status-run'

describe('bulk status run', () => {
  it('sends every chunk in order and reports progress', async () => {
    const send = vi.fn().mockResolvedValue(undefined)
    const progress = vi.fn()
    const result = await runInChunks(['a', 'b', 'c'], 2, send, progress)
    expect(send.mock.calls).toEqual([[['a', 'b']], [['c']]])
    expect(progress.mock.calls).toEqual([[2, 3], [3, 3]])
    expect(bulkStatusResultMessage('Aktif', result)).toEqual({ title: 'Ürün Durumları Güncellendi', message: '3 ürün durumu “Aktif” olarak güncellendi.', kind: 'success' })
  })

  it('stops at a failed chunk and names the partial result', async () => {
    const send = vi.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce(new Error('Sunucu hatası.'))
    const result = await runInChunks(['a', 'b', 'c', 'd', 'e'], 2, send)
    expect(result.doneIds).toEqual(['a', 'b'])
    expect(result.remainingIds).toEqual(['c', 'd', 'e'])
    expect(send).toHaveBeenCalledTimes(2)
    expect(bulkStatusResultMessage('Arşiv', result)).toEqual({ title: 'Ürün Durumları Kısmen Güncellendi', message: '2 ürün “Arşiv” olarak güncellendi; kalan 3 ürün güncellenemedi: Sunucu hatası.', kind: 'error' })
  })

  it('reports a failure of the first chunk as a plain error', async () => {
    const result = await runInChunks(['a'], 500, () => Promise.reject(new Error('Yetki yok.')))
    expect(bulkStatusResultMessage('Aktif', result)).toEqual({ title: 'Ürün Durumları Güncellenemedi', message: 'Yetki yok.', kind: 'error' })
  })
})

describe('bulk status plan', () => {
  const statuses: Record<string, string> = { a: 'ACTIVE', b: 'ACTIVE', c: 'DRAFT', d: 'ARCHIVED' }
  const statusOf = (id: string) => statuses[id]

  it('leaves out records already in the target status', () => {
    expect(bulkStatusPlan(['a', 'b', 'c', 'd'], statusOf, 'ACTIVE')).toEqual({ targetIds: ['c', 'd'], alreadyIds: ['a', 'b'] })
    expect(bulkStatusPlan(['a', 'b', 'c', 'd'], statusOf, 'ARCHIVED')).toEqual({ targetIds: ['a', 'b', 'c'], alreadyIds: ['d'] })
  })

  it('has nothing to send when every selected record is already there', () => {
    expect(bulkStatusPlan(['a', 'b'], statusOf, 'ACTIVE').targetIds).toEqual([])
  })

  it('keeps a record whose status is unknown', () => {
    expect(bulkStatusPlan(['a', 'x'], statusOf, 'ACTIVE')).toEqual({ targetIds: ['x'], alreadyIds: ['a'] })
  })
})
