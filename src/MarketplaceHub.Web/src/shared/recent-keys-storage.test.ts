import { afterEach, describe, expect, it, vi } from 'vitest'
import { readRecentKeys, recentKeyDefaultMaxAgeMs, rememberRecentKey } from './recent-keys-storage'

const storageKey = 'ravencia.printedShippingLabels'
const day = 24 * 60 * 60 * 1000

afterEach(() => { localStorage.clear(); vi.restoreAllMocks() })

describe('recent keys in browser storage (WEB-ALTYAPI-5)', () => {
  it('keeps at most the limit: the 3001st key drops the oldest', () => {
    localStorage.setItem(storageKey, JSON.stringify(Array.from({ length: 3000 }, (_, index) => ({ key: `o${index}:a4`, at: 1000 + index }))))
    rememberRecentKey(storageKey, 'new:a4', {}, 5000)
    const keys = readRecentKeys(storageKey)
    expect(keys).toHaveLength(3000)
    expect(keys[0]).toBe('o1:a4')
    expect(keys.at(-1)).toBe('new:a4')
  })

  it('reads the older plain list and keeps its keys, as recent, when the next key is added', () => {
    localStorage.setItem(storageKey, JSON.stringify(['o1:a4', 'o2:sticker']))
    expect(readRecentKeys(storageKey)).toEqual(['o1:a4', 'o2:sticker'])
    rememberRecentKey(storageKey, 'o3:a4', {}, Date.now())
    expect(readRecentKeys(storageKey)).toEqual(['o1:a4', 'o2:sticker', 'o3:a4'])
    expect(JSON.parse(localStorage.getItem(storageKey)!)[0]).toMatchObject({ key: 'o1:a4' })
  })

  it('drops keys older than the age limit on the next write, keeps recent ones', () => {
    const now = 100 * day
    localStorage.setItem(storageKey, JSON.stringify([{ key: 'old:a4', at: now - recentKeyDefaultMaxAgeMs - 1 }, { key: 'recent:a4', at: now - day }]))
    rememberRecentKey(storageKey, 'today:a4', {}, now)
    expect(readRecentKeys(storageKey)).toEqual(['recent:a4', 'today:a4'])
  })

  it('moves a key printed again to the end instead of listing it twice', () => {
    rememberRecentKey(storageKey, 'o1:a4', {}, 1)
    rememberRecentKey(storageKey, 'o2:a4', {}, 2)
    rememberRecentKey(storageKey, 'o1:a4', {}, 3)
    expect(readRecentKeys(storageKey, 3)).toEqual(['o2:a4', 'o1:a4'])
  })

  it('parses the stored list once while it does not change', () => {
    rememberRecentKey(storageKey, 'o1:a4')
    const parse = vi.spyOn(JSON, 'parse')
    for (let row = 0; row < 50; row++) readRecentKeys(storageKey)
    expect(parse).not.toHaveBeenCalled()
    localStorage.setItem(storageKey, JSON.stringify(['other:a4']))
    expect(readRecentKeys(storageKey)).toEqual(['other:a4'])
  })

  it('still knows the new key on this page when storage refuses the write', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('full', 'QuotaExceededError') })
    vi.spyOn(console, 'warn').mockImplementation(() => {})
    rememberRecentKey(storageKey, 'o9:sticker')
    expect(readRecentKeys(storageKey)).toEqual(['o9:sticker'])
  })
})
