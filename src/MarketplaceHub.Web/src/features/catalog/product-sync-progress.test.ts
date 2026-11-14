import { describe, expect, it } from 'vitest'
import { productSyncLabel, productSyncPercent } from './product-sync-progress'

const job = { progressReceived: 1018, progressTotal: null, progressPercent: null, progressProcessed: 587, progressSkipped: 0, progressFailed: 0 }

describe('productSyncPercent', () => {
  it('is unknown without a total or a server percent', () => {
    expect(productSyncPercent(job)).toBeNull()
  })
  it('sets finished records against the total', () => {
    expect(productSyncPercent({ ...job, progressTotal: 1018 })).toBe(57)
    expect(productSyncPercent({ ...job, progressTotal: 1018, progressSkipped: 10, progressFailed: 5 })).toBe(59)
  })
  it('prefers the server percent and clamps it', () => {
    expect(productSyncPercent({ ...job, progressPercent: 58 })).toBe(58)
    expect(productSyncPercent({ ...job, progressPercent: 140 })).toBe(100)
  })
  it('ignores a total below the received count', () => {
    expect(productSyncPercent({ ...job, progressTotal: 10 })).toBeNull()
  })
})

describe('productSyncLabel', () => {
  it('drops the leading count and groups Turkish style', () => {
    expect(productSyncLabel('1,018 · Alınan 1,018 · İşlenen 587 · Atlanan 0 · Hatalı 0')).toBe('Alınan 1.018 · İşlenen 587 · Atlanan 0 · Hatalı 0')
    expect(productSyncLabel('1,018 / 5,000 · Alınan 1,018 · Hatalı 0')).toBe('Alınan 1.018 · Hatalı 0')
  })
  it('keeps a label without counts', () => {
    expect(productSyncLabel('Kuyrukta bekliyor')).toBe('Kuyrukta bekliyor')
  })
})
