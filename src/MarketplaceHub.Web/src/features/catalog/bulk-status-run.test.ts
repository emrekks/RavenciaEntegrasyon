import { describe, expect, it, vi } from 'vitest'
import { bulkStatusResultMessage, runInChunks } from './bulk-status-run'

describe('bulk status run', () => {
  it('sends every chunk in order and reports progress', async () => {
    const send = vi.fn().mockResolvedValue(undefined)
    const progress = vi.fn()
    const result = await runInChunks(['a', 'b', 'c'], 2, send, progress)
    expect(send.mock.calls).toEqual([[['a', 'b']], [['c']]])
    expect(progress.mock.calls).toEqual([[2, 3], [3, 3]])
    expect(bulkStatusResultMessage('Aktif', result)).toEqual({ message: '3 ürün durumu “Aktif” olarak güncellendi.', kind: 'success' })
  })

  it('stops at a failed chunk and names the partial result', async () => {
    const send = vi.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce(new Error('Sunucu hatası.'))
    const result = await runInChunks(['a', 'b', 'c', 'd', 'e'], 2, send)
    expect(result.doneIds).toEqual(['a', 'b'])
    expect(result.remainingIds).toEqual(['c', 'd', 'e'])
    expect(send).toHaveBeenCalledTimes(2)
    expect(bulkStatusResultMessage('Arşiv', result)).toEqual({ message: '2 ürün “Arşiv” olarak güncellendi; kalan 3 ürün güncellenemedi: Sunucu hatası.', kind: 'error' })
  })

  it('reports a failure of the first chunk as a plain error', async () => {
    const result = await runInChunks(['a'], 500, () => Promise.reject(new Error('Yetki yok.')))
    expect(bulkStatusResultMessage('Aktif', result)).toEqual({ message: 'Yetki yok.', kind: 'error' })
  })
})
