import { beforeEach, describe, expect, it } from 'vitest'
import { initialSort, readStickySort, rememberSort } from './sticky-sort'

const options = ['NEWEST', 'OLDEST', 'AMOUNT'] as const

describe('sticky sort', () => {
  beforeEach(() => { localStorage.clear() })

  it('comes back after the page is opened again', () => {
    expect(initialSort('returns', options, 'NEWEST')).toBe('NEWEST')
    rememberSort('returns', 'AMOUNT')
    expect(initialSort('returns', options, 'NEWEST')).toBe('AMOUNT')
    expect(initialSort('orders', options, 'NEWEST')).toBe('NEWEST')
  })

  it('lets the address win and falls back to the default for a stored value that is no longer offered', () => {
    rememberSort('returns', 'AMOUNT')
    expect(initialSort('returns', options, 'NEWEST', 'oldest')).toBe('OLDEST')
    rememberSort('returns', 'REMOVED_SORT')
    expect(readStickySort('returns', options)).toBeNull()
    expect(initialSort('returns', options, 'NEWEST')).toBe('NEWEST')
  })

  it('survives a storage that refuses', () => {
    const original = Storage.prototype.getItem
    Storage.prototype.getItem = () => { throw new Error('blocked') }
    try { expect(initialSort('returns', options, 'NEWEST')).toBe('NEWEST') } finally { Storage.prototype.getItem = original }
  })
})

import { rememberPageSize, storedPageSize } from './sticky-sort'
describe('sticky page size', () => {
  beforeEach(() => { localStorage.clear() })
  it('opens the page with the last picked size, and falls back for one no longer offered', () => {
    expect(storedPageSize('orders', [20, 50, 100], 50)).toBe(50)
    rememberPageSize('orders', 100)
    expect(storedPageSize('orders', [20, 50, 100], 50)).toBe(100)
    expect(storedPageSize('orders', [20, 50], 50)).toBe(50)
    expect(storedPageSize('returns', [20, 50, 100], 20)).toBe(20)
  })
})
