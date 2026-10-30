import { describe, expect, it } from 'vitest'
import { bulkVatRate, bulkVatResultMessage } from './bulk-vat-run'

describe('bulk VAT', () => {
  it('sends only an offered rate and never reads an empty choice as a rate', () => {
    expect(bulkVatRate('10')).toBe(10)
    expect(bulkVatRate(' 20 ')).toBe(20)
    expect(bulkVatRate('1')).toBe(1)
    expect(bulkVatRate('')).toBeNull()
    expect(bulkVatRate('0')).toBe(0)
    expect(bulkVatRate('8')).toBeNull()
    expect(bulkVatRate('18')).toBeNull()
  })

  it('names updated and unchanged records and says listings keep their VAT', () => {
    expect(bulkVatResultMessage({ doneIds: ['a', 'b', 'c'], remainingIds: [], error: null }, { updatedCount: 2, unchangedCount: 1 }))
      .toEqual({ title: 'KDV Atandı', message: '2 kayıt güncellendi, 1 kayıt zaten bu KDV’deydi. Pazar yerlerindeki ilanların KDV’si değişmedi.', kind: 'success' })
    expect(bulkVatResultMessage({ doneIds: ['a'], remainingIds: [], error: null }, { updatedCount: 1, unchangedCount: 0 }).message)
      .toBe('1 kayıt güncellendi. Pazar yerlerindeki ilanların KDV’si değişmedi.')
  })

  it('reports a stopped run with what went through and what did not', () => {
    expect(bulkVatResultMessage({ doneIds: ['a', 'b'], remainingIds: ['c'], error: new Error('Sunucu hatası.') }, { updatedCount: 2, unchangedCount: 0 }))
      .toEqual({ title: 'KDV Kısmen Atandı', message: '2 kayıt güncellendi; kalan 1 kayıt güncellenemedi: Sunucu hatası.', kind: 'error' })
    expect(bulkVatResultMessage({ doneIds: [], remainingIds: ['a'], error: new Error('Yetki yok.') }, { updatedCount: 0, unchangedCount: 0 }))
      .toEqual({ title: 'KDV Atanamadı', message: 'Yetki yok.', kind: 'error' })
  })
})
