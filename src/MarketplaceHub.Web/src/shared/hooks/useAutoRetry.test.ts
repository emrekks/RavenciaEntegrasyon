import { describe, expect, it } from 'vitest'
import { autoRetryDelaySeconds, autoRetryLine, type AutoRetry } from './useAutoRetry'

const retry = (patch: Partial<AutoRetry>): AutoRetry => ({ left: 10, total: 10, stopped: false, retrying: false, now: () => {}, stop: () => {}, ...patch })

describe('automatic retry of a failed load', () => {
  it('waits longer after every failed try and settles at one minute', () => {
    expect([0, 1, 2, 3, 4, 9].map(autoRetryDelaySeconds)).toEqual([10, 20, 40, 60, 60, 60])
  })

  it('tells what happens next', () => {
    expect(autoRetryLine(retry({ left: 7 }))).toBe('7 saniye içinde yeniden denenecek.')
    expect(autoRetryLine(retry({ left: 0, retrying: true }))).toBe('Yeniden deneniyor…')
    expect(autoRetryLine(retry({ stopped: true }))).toBe('Otomatik deneme durduruldu.')
  })
})
