import { afterEach, describe, expect, it, vi } from 'vitest'
import { readStoredItem, removeStoredItem, writeStoredItem } from './local-storage'

afterEach(() => { vi.restoreAllMocks(); localStorage.clear() })

describe('local storage helpers', () => {
  it('read, write and remove like localStorage while it works', () => {
    writeStoredItem('k', 'v')
    expect(readStoredItem('k')).toBe('v')
    removeStoredItem('k')
    expect(readStoredItem('k')).toBeNull()
  })

  it('read blocked storage as empty and drop writes instead of throwing', () => {
    const blocked = () => { throw new DOMException('blocked', 'SecurityError') }
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(blocked)
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(blocked)
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(blocked)
    expect(readStoredItem('k')).toBeNull()
    expect(() => writeStoredItem('k', 'v')).not.toThrow()
    expect(() => removeStoredItem('k')).not.toThrow()
  })
})
