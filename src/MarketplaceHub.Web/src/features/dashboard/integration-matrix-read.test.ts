import { describe, expect, it } from 'vitest'
import { readIntegrationMatrix } from './IntegrationStatusPanel'

describe('the integration matrix as the panel reads it', () => {
  // The contract gives a cell's state as a string; the panel knows ok, warn, bad and off. A state it does not know is
  // left out (no data) rather than shown as one of those.
  it('keeps the known cell states and leaves out an unknown one', () => {
    const row = { connectionId: 'c1', platformCode: 'TRENDYOL', displayName: 'T', externalStoreId: '1', status: 'ACTIVE', issue: null,
      cells: { orders: { state: 'ok', lastSuccessAt: '2026-11-16T00:00:00Z', lastProblemAt: null }, returns: { state: 'later', lastSuccessAt: null, lastProblemAt: null } } }
    const [read] = readIntegrationMatrix([row])
    expect(Object.keys(read.cells)).toEqual(['orders'])
    expect(read.cells.orders.state).toBe('ok')
    expect(read.displayName).toBe('T')
  })
})
