import { describe, expect, it } from 'vitest'
import type { ConnectionFullResetView } from '../marketplace/connection-data-reset'
import { marketplaceResetCount, marketplaceResetTotal, progressText, runMarketplaceResets, type BulkResetProgress } from './marketplace-bulk-reset'

const view = (status: string | null, percent: number | null = null, label: string | null = null, jobId = 'j1'): ConnectionFullResetView =>
  ({ items: [], keptProducts: 0, job: status ? { jobId, status, progressLabel: label, progressPercent: percent, createdAt: '2026-11-12T10:00:00Z' } : null })

describe('marketplace bulk reset', () => {
  it('counts a connection without its product links and totals the chosen ones', () => {
    expect(marketplaceResetCount({ items: [{ scope: 'ORDERS', count: 10 }, { scope: 'PRODUCT_LINKS', count: 99 }, { scope: 'FINANCE', count: 5 }] })).toBe(15)
    expect(marketplaceResetCount(undefined)).toBeUndefined()
    expect(marketplaceResetTotal(['a', 'b'], { a: 15, b: 7, c: 100 })).toBe(22)
    expect(marketplaceResetTotal(['a', 'b'], { a: 15 })).toBeUndefined()
    expect(marketplaceResetTotal([], {})).toBe(0)
  })

  it('writes the progress in words', () => {
    expect(progressText({ state: 'running', label: 'Siparişler siliniyor', percent: 40 })).toBe('Siparişler siliniyor · %40')
    expect(progressText({ state: 'running', label: null, percent: null })).toBe('Sıfırlama başlatıldı')
    expect(progressText({ state: 'done' })).toBe('Tamamlandı')
    expect(progressText(undefined)).toBeNull()
  })

  it('requests one connection at a time and follows each job to its end', async () => {
    const log: string[] = []
    const progress: Record<string, BulkResetProgress[]> = {}
    const polls: Record<string, ConnectionFullResetView[]> = { a: [view('LEASED', 50, 'Siparişler'), view('SUCCEEDED', 100)], b: [view('SUCCEEDED', 100, null, 'j2')] }
    const ok = await runMarketplaceResets([{ id: 'a', displayName: 'A', version: 1 }, { id: 'b', displayName: 'B', version: 2 }], {
      request: async item => { log.push(`request ${item.id}`); return view('PENDING', 0, 'Sırada', item.id === 'a' ? 'j1' : 'j2') },
      poll: async item => { log.push(`poll ${item.id}`); return polls[item.id].shift()! },
      sleep: async () => undefined,
      onProgress: (id, state) => { (progress[id] ??= []).push(state) }
    })
    expect(ok).toBe(true)
    // b is not requested before a has ended
    expect(log).toEqual(['request a', 'poll a', 'poll a', 'request b', 'poll b'])
    expect(progress.a.map(state => state.state)).toEqual(['waiting', 'running', 'running', 'done'])
    expect(progress.b.at(-1)).toEqual({ state: 'done' })
  })

  it('stops at the first failed connection and leaves the rest alone', async () => {
    const requested: string[] = []
    const progress: Record<string, BulkResetProgress> = {}
    const ok = await runMarketplaceResets([{ id: 'a', displayName: 'A', version: 1 }, { id: 'b', displayName: 'B', version: 1 }], {
      request: async item => { requested.push(item.id); return view('PENDING') },
      poll: async () => view('DEAD', 10, 'Kilit beklendi'),
      sleep: async () => undefined,
      onProgress: (id, state) => { progress[id] = state }
    })
    expect(ok).toBe(false)
    expect(requested).toEqual(['a'])
    expect(progress.a).toEqual({ state: 'failed', message: 'Sıfırlama tamamlanamadı: Kilit beklendi' })
    expect(progress.b).toEqual({ state: 'waiting' })
  })

  it('reports a refused request on its row', async () => {
    const progress: Record<string, BulkResetProgress> = {}
    const ok = await runMarketplaceResets([{ id: 'a', displayName: 'A', version: 1 }], {
      request: async () => { throw new Error('Kayıt sürümü değişti.') }, poll: async () => view('SUCCEEDED'), sleep: async () => undefined,
      onProgress: (id, state) => { progress[id] = state }
    })
    expect(ok).toBe(false)
    expect(progress.a).toEqual({ state: 'failed', message: 'Kayıt sürümü değişti.' })
  })
})
