import { describe, expect, it } from 'vitest'
import { sendStockDisposition, type PendingDispositionKeys } from './return-disposition-key'

class HttpError extends Error {
  constructor(public readonly status: number) { super(`HTTP ${status}`) }
}

describe('return stock disposition keys', () => {
  it('resends a decision whose answer was lost with the same key', async () => {
    const pending: PendingDispositionKeys = new Map()
    const keys: string[] = []
    await expect(sendStockDisposition(pending, 'r1', 'l1', '{"q":1}', async key => { keys.push(key); throw new TypeError('Failed to fetch') })).rejects.toThrow()
    await sendStockDisposition(pending, 'r1', 'l1', '{"q":1}', async key => { keys.push(key); return 'ok' })
    expect(keys[0]).toBe(keys[1])
    expect(keys[0]).toMatch(/^return-stock-disposition:r1:/u)
  })

  it('gives a new decision a new key: after a success, after an HTTP answer, or with another body', async () => {
    const pending: PendingDispositionKeys = new Map()
    const keys: string[] = []
    const ok = async (key: string) => { keys.push(key); return 'ok' }
    await sendStockDisposition(pending, 'r1', 'l1', '{"q":1}', ok)
    await sendStockDisposition(pending, 'r1', 'l1', '{"q":1}', ok)
    await expect(sendStockDisposition(pending, 'r1', 'l1', '{"q":1}', async key => { keys.push(key); throw new HttpError(422) })).rejects.toThrow()
    await sendStockDisposition(pending, 'r1', 'l1', '{"q":1}', ok)
    await expect(sendStockDisposition(pending, 'r1', 'l1', '{"q":1}', async key => { keys.push(key); throw new TypeError('Failed to fetch') })).rejects.toThrow()
    await sendStockDisposition(pending, 'r1', 'l1', '{"q":2}', ok)
    expect(new Set(keys).size).toBe(keys.length)
    expect(pending.size).toBe(0)
  })
})
