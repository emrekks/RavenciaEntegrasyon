import { describe, expect, it, vi } from 'vitest'
import { activeReturnSyncConnections, enqueueReturnSyncs } from './return-sync'

describe('read-only return synchronization', () => {
  it('includes active Trendyol and Hepsiburada connections only', () => {
    const connections = [
      { id: 'trendyol', platformCode: 'TRENDYOL', status: 'ACTIVE' },
      { id: 'hepsiburada', platformCode: 'HEPSIBURADA', status: 'VERIFIED' },
      { id: 'inactive', platformCode: 'HEPSIBURADA', status: 'DISABLED' },
      { id: 'shopify', platformCode: 'SHOPIFY', status: 'ACTIVE' }
    ]

    expect(activeReturnSyncConnections(connections).map(connection => connection.id)).toEqual(['trendyol', 'hepsiburada'])
  })

  it('queues every active connection and reports partial failures', async () => {
    const connections = [
      { id: 'trendyol', platformCode: 'TRENDYOL', status: 'ACTIVE' },
      { id: 'hepsiburada', platformCode: 'HEPSIBURADA', status: 'ACTIVE' }
    ]
    const enqueue = vi.fn(async (connection: typeof connections[number]) => {
      if (connection.id === 'hepsiburada') throw new Error('temporary failure')
      return connection.id
    })

    await expect(enqueueReturnSyncs(connections, enqueue)).resolves.toEqual({ succeeded: 1, failed: 1 })
    expect(enqueue).toHaveBeenCalledTimes(2)
    expect(enqueue.mock.calls.map(([connection]) => connection.id)).toEqual(['trendyol', 'hepsiburada'])
  })

  it('fails clearly when no connection can start a sync', async () => {
    await expect(enqueueReturnSyncs([], async () => undefined)).rejects.toThrow('Aktif veya doğrulanmış bir iade bağlantısı bulunamadı.')
    await expect(enqueueReturnSyncs([{ id: 'hepsiburada', platformCode: 'HEPSIBURADA', status: 'ACTIVE' }], async () => { throw new Error('offline') }))
      .rejects.toThrow('Hiçbir iade bağlantısında eşitleme başlatılamadı.')
  })
})
